using System.Collections;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using XamlG.Runtime;
using FrameworkSourceInfo = Avalonia.Markup.Xaml.Diagnostics.XamlSourceInfo;

namespace XamlG.AvaloniaRuntime.Inspection;

public sealed partial class AvaloniaRuntimeInspector
{
    /// <summary>Reads framework and generated-object provenance. Keyed resource metadata
    /// is available before a deferred resource is instantiated.</summary>
    public RuntimeSourceInspection Source(string objectId, IReadOnlyList<string>? path = null, RuntimeArgument? resourceKey = null)
    {
        var target = FollowPath(Resolve(objectId), path);
        FrameworkSourceInfo? frameworkSource; XamlSourceInfo? source = null;
        if (resourceKey != null)
        {
            var dictionary = target as IResourceDictionary ?? (target as StyledElement)?.Resources
                ?? throw new ArgumentException("The object path must select a resource dictionary or styled element.");
            var key = ConvertArgument(resourceKey, typeof(object)) ?? throw new ArgumentException("A resource key cannot be null.");
            frameworkSource = FrameworkSourceInfo.GetXamlSourceInfo(dictionary, key);
        }
        else
        {
            frameworkSource = target == null ? null : FrameworkSourceInfo.GetXamlSourceInfo(target);
            if (target != null)
                foreach (var owner in _objects.Values)
                    if (XamlRuntimeSession.TryGet(owner, out var session) && session!.FindNode(target)?.Source is { } found)
                    { source = found; break; }
        }
        return new(Revision, frameworkSource == null ? null : new(frameworkSource.SourceUri?.OriginalString, frameworkSource.LineNumber, frameworkSource.LinePosition), source, resourceKey != null);
    }

    /// <summary>Read one level of a live object path, including DataContext, collections and public application members.</summary>
    public RuntimeObjectInspection InspectObject(string objectId, IReadOnlyList<string>? path = null, int offset = 0, int count = 100, bool includeNonPublic = false)
    {
        if (offset is < 0 or > 100000 || count is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(offset));
        var target = FollowPath(Resolve(objectId), path);
        if (target == null) return new(Revision, DescribeValue(null), 0, 0, false, [], []);
        var flags = MemberFlags(includeNonPublic);
        var members = Members(target, flags).ToArray();
        if (members.Length > 100000) throw new InvalidOperationException("Too many object members.");
        var result = members.Skip(offset).Take(count).Select(member =>
        {
            try { return new RuntimeMember(member.Name, TypeName(member.Type), member.Kind, member.Set == null, DescribeValue(member.Get())); }
            catch (Exception error) { return new RuntimeMember(member.Name, TypeName(member.Type), member.Kind, member.Set == null, null, ErrorText(error)); }
        }).ToArray();
        var methods = target.GetType().GetMethods(flags).Where(Callable).Select(Signature).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Take(500).ToArray();
        return new(Revision, DescribeValue(target), members.Length, offset, offset + result.Length < members.Length, result, methods);
    }

    public RuntimeValue ReadObject(string objectId, IReadOnlyList<string> path) => DescribeValue(FollowPath(Resolve(objectId), path));

    public RuntimeValue SetObjectMember(string objectId, IReadOnlyList<string> path, RuntimeArgument argument, long expectedRevision)
    {
        var root = ResolveForMutation(objectId, expectedRevision);
        var member = WritablePath(root, path);
        var value = ConvertArgument(argument, member.Type);
        // Resolving a referenced tree object can discover an intervening topology change.
        ResolveForMutation(objectId, expectedRevision);
        member.Set!(value); Changed(objectId, "member", string.Join('.', path), DescribeValue(value));
        return DescribeValue(member.Get());
    }

    public RuntimeValue CreateObjectMember(string objectId, IReadOnlyList<string> path, string typeName,
        IReadOnlyDictionary<string, RuntimeArgument>? initialValues, long expectedRevision)
    {
        var root = ResolveForMutation(objectId, expectedRevision);
        var member = WritablePath(root, path);
        var type = ResolveType(typeName);
        if (!member.Type.IsAssignableFrom(type)) throw new ArgumentException("The requested type is not assignable to this member.");
        var value = Construct(type, initialValues);
        ResolveForMutation(objectId, expectedRevision);
        member.Set!(value); Changed(objectId, "member", string.Join('.', path), DescribeValue(value));
        return DescribeValue(value);
    }

    /// <summary>Invokes an exact public signature; getters/setters are exposed by member tools.</summary>
    public async Task<RuntimeValue> InvokeMethodAsync(string objectId, IReadOnlyList<string>? path, string signature,
        IReadOnlyList<RuntimeArgument> arguments, long expectedRevision, CancellationToken cancellationToken = default)
    {
        var target = FollowPath(ResolveForMutation(objectId, expectedRevision), path) ?? throw new InvalidOperationException("The target is null.");
        if (arguments.Count > 32) throw new ArgumentException("At most 32 method arguments are supported.");
        var method = target.GetType().GetMethods(MemberFlags(false)).Where(Callable).SingleOrDefault(method => Signature(method) == signature)
            ?? throw new ArgumentException("Use an exact method signature returned by object inspection.");
        var parameters = method.GetParameters();
        if (parameters.Length != arguments.Count) throw new ArgumentException("The argument count does not match the signature.");
        var values = parameters.Select((parameter, index) => ConvertArgument(arguments[index], parameter.ParameterType)).ToArray();
        ResolveForMutation(objectId, expectedRevision); cancellationToken.ThrowIfCancellationRequested();
        object? result;
        try { result = method.Invoke(target, values); }
        catch (TargetInvocationException error) { throw new InvalidOperationException(ErrorText(error), error.InnerException); }
        Changed(objectId, "method", signature, null);
        // A boxed ValueTask must be consumed exactly once. Convert generic ValueTask<T>
        // through its public AsTask API before awaiting and reading the result.
        if (result is ValueTask valueTask) result = valueTask.AsTask();
        else if (result != null && method.ReturnType.IsGenericType && method.ReturnType.GetGenericTypeDefinition() == typeof(ValueTask<>))
            result = method.ReturnType.GetMethod(nameof(ValueTask.AsTask))!.Invoke(result, null);
        if (result is Task task)
        {
            await task.WaitAsync(cancellationToken);
            VerifyAccess();
            result = method.ReturnType.IsGenericType ? task.GetType().GetProperty("Result")?.GetValue(task) : null;
        }
        return DescribeValue(result);
    }

    public IReadOnlyList<RuntimeType> Types(string query = "", int offset = 0, int count = 100)
    {
        VerifyAccess();
        if (offset is < 0 or > 100000 || count is < 1 or > 500 || query.Length > 512) throw new ArgumentException("Invalid type query or range.");
        return LoadedTypes().Where(type => TypeName(type).Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(TypeName, StringComparer.Ordinal).ThenBy(type => type.Assembly.GetName().Name, StringComparer.Ordinal).Skip(offset).Take(count)
            .Select(type => new RuntimeType(TypeName(type), type.Assembly.GetName().Name ?? "", type.BaseType == null ? null : TypeName(type.BaseType), CanConstruct(type), typeof(Control).IsAssignableFrom(type), type.AssemblyQualifiedName)).ToArray();
    }

    private object? ConvertArgument(RuntimeArgument argument, Type type)
    {
        if (argument.ObjectId != null)
        {
            if (argument.Value is { ValueKind: not (System.Text.Json.JsonValueKind.Undefined or System.Text.Json.JsonValueKind.Null) })
                throw new ArgumentException("Supply either a literal value or a live object reference.");
            var value = FollowPath(Resolve(argument.ObjectId), argument.Path);
            if (value != null && !type.IsInstanceOfType(value)) throw new ArgumentException("The referenced object is not assignable to " + TypeName(type));
            if (value == null && type.IsValueType && Nullable.GetUnderlyingType(type) == null) throw new ArgumentException("Null is not valid for this member.");
            return value;
        }
        if (argument.Path is { Length: > 0 }) throw new ArgumentException("A reference path requires an object ID.");
        return ConvertValue(argument.Value ?? System.Text.Json.JsonSerializer.SerializeToElement<object?>(null), type);
    }

    private object Construct(Type type, IReadOnlyDictionary<string, RuntimeArgument>? initialValues)
    {
        if (!CanConstruct(type)) throw new ArgumentException("The type requires a public parameterless constructor and must be concrete.");
        if (initialValues?.Count > 256) throw new ArgumentException("Too many initial values.");
        var value = Activator.CreateInstance(type)!;
        if (initialValues != null)
            foreach (var (key, argument) in initialValues)
            {
                // Ordinary CLR names take precedence over unrelated attached properties with
                // the same short name (for example Name and AutomationProperties.Name).
                var clrName = key.StartsWith("clr:", StringComparison.Ordinal) ? key[4..] : key;
                var clrProperty = value.GetType().GetProperty(clrName, BindingFlags.Public | BindingFlags.Instance);
                if (clrProperty?.SetMethod?.IsPublic == true && clrProperty.GetIndexParameters().Length == 0)
                    clrProperty.SetValue(value, ConvertArgument(argument, clrProperty.PropertyType));
                else if (value is AvaloniaObject avalonia && !key.StartsWith("clr:", StringComparison.Ordinal) && Registered(avalonia).Any(property => Key(property) == key || property.Name == key))
                {
                    var property = FindProperty(avalonia, key);
                    if (property.IsReadOnly) throw new InvalidOperationException("The initial property is read-only.");
                    avalonia.SetValue(property, ConvertArgument(argument, property.PropertyType));
                }
                else
                {
                    var member = FindMember(value, key.StartsWith("clr:", StringComparison.Ordinal) ? key[4..] : key);
                    if (member.Set == null) throw new InvalidOperationException("The initial member is read-only.");
                    member.Set(ConvertArgument(argument, member.Type));
                }
            }
        return value;
    }

    private static object? FollowPath(object? root, IReadOnlyList<string>? path)
    {
        ValidatePath(path);
        foreach (var segment in path ?? []) root = FindMember(root ?? throw new InvalidOperationException("A path member is null."), segment).Get();
        return root;
    }
    private static MemberAccessor WritablePath(object root, IReadOnlyList<string> path)
    {
        ValidatePath(path);
        if (path.Count == 0) throw new ArgumentException("Select a member below the root object.");
        var parent = FollowPath(root, path.Take(path.Count - 1).ToArray()) ?? throw new InvalidOperationException("The parent member is null.");
        var member = FindMember(parent, path[^1]);
        if (member.Set == null) throw new InvalidOperationException("The member is read-only.");
        return member;
    }
    private static void ValidatePath(IReadOnlyList<string>? path)
    {
        if (path?.Count > 32 || path?.Any(segment => string.IsNullOrEmpty(segment) || segment.Length > 1024) == true)
            throw new ArgumentException("Use at most 32 property names, dictionary keys or numeric indices.");
    }
    private static MemberAccessor FindMember(object target, string name) => Members(target, MemberFlags(false)).FirstOrDefault(member => member.Name == name)
        ?? throw new ArgumentException("Unknown public object member: " + name);
    private static IEnumerable<MemberAccessor> Members(object target, BindingFlags flags)
    {
        if (target is IDictionary dictionary)
        {
            foreach (var key in dictionary.Keys.Cast<object>().Take(100001))
            {
                if (key is not string name) continue;
                var valueType = DictionaryValueType(target.GetType());
                yield return new(name, valueType, "dictionary", () => dictionary[key], dictionary.IsReadOnly ? null : value => dictionary[key] = value);
            }
            yield break;
        }
        if (target is IList list)
        {
            var elementType = target.GetType().IsArray ? target.GetType().GetElementType()! : target.GetType().GetInterfaces()
                .FirstOrDefault(type => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IList<>))?.GenericTypeArguments[0] ?? typeof(object);
            for (var i = 0; i < Math.Min(list.Count, 100001); i++)
            {
                var index = i;
                yield return new(index.ToString(System.Globalization.CultureInfo.InvariantCulture), elementType, "item", () => list[index], list.IsReadOnly ? null : value => list[index] = value);
            }
            yield break;
        }
        foreach (var property in target.GetType().GetProperties(flags).Where(property => property.GetIndexParameters().Length == 0 && property.GetMethod != null).OrderBy(property => property.Name, StringComparer.Ordinal))
            yield return new(property.Name, property.PropertyType, "property", () => property.GetValue(target), property.SetMethod?.IsPublic == true ? value => property.SetValue(target, value) : null);
        foreach (var field in target.GetType().GetFields(flags).OrderBy(field => field.Name, StringComparer.Ordinal))
            yield return new(field.Name, field.FieldType, "field", () => field.GetValue(target), field.IsPublic && !field.IsInitOnly && !field.IsLiteral ? value => field.SetValue(target, value) : null);
    }
    private static Type DictionaryValueType(Type type) => type.GetInterfaces().FirstOrDefault(item => item.IsGenericType && item.GetGenericTypeDefinition() == typeof(IDictionary<,>))?.GenericTypeArguments[1] ?? typeof(object);
    private static BindingFlags MemberFlags(bool includeNonPublic) => BindingFlags.Instance | BindingFlags.Public | (includeNonPublic ? BindingFlags.NonPublic : 0);
    private static bool Callable(MethodInfo method) => method.IsPublic && !method.IsSpecialName && !method.ContainsGenericParameters && !method.ReturnType.IsByRefLike && !method.ReturnType.IsByRef && !method.ReturnType.IsPointer &&
        method.GetParameters().All(parameter => !parameter.ParameterType.IsByRef && !parameter.ParameterType.IsByRefLike && !parameter.ParameterType.IsPointer);
    private static string Signature(MethodInfo method) => method.Name + "(" + string.Join(",", method.GetParameters().Select(parameter => TypeName(parameter.ParameterType))) + ")";
    private static bool CanConstruct(Type type) => (type.IsPublic || type.IsNestedPublic) && !type.IsAbstract && !type.IsInterface && !type.ContainsGenericParameters && !type.IsByRefLike && (type.IsValueType || type.GetConstructor(Type.EmptyTypes) != null);
    private static IEnumerable<Type> LoadedTypes() => AppDomain.CurrentDomain.GetAssemblies().SelectMany(assembly =>
    {
        try { return assembly.GetExportedTypes(); }
        catch (ReflectionTypeLoadException error) { return error.Types.OfType<Type>().Where(type => type.IsPublic || type.IsNestedPublic); }
        catch (NotSupportedException) { return []; }
    });
    private static Type ResolveType(string name)
    {
        var matches = LoadedTypes().Where(type => TypeName(type) == name || type.AssemblyQualifiedName == name).Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : throw new ArgumentException("Unknown or ambiguous type. Use an inspected full or assembly-qualified type name.");
    }
    private sealed record MemberAccessor(string Name, Type Type, string Kind, Func<object?> Get, Action<object?>? Set);
}
