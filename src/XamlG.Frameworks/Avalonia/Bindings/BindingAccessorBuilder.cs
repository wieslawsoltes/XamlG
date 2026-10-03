using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using XamlG.Compiler;
using XamlG.Frameworks.Avalonia.Styling;
using XamlG.Roslyn;
using XamlG.Syntax;

namespace XamlG.Frameworks.Avalonia.Bindings;

internal sealed class BindingAccessorBuilder(BindingContext context)
{
    public BindingAccessor? Property(ITypeSymbol sourceType, ISymbol member, ImmutableArray<BoundExpression> indices, TextSpan span)
    {
        var expressions = new BindingExpressionFactory(context);
        var objectType = context.Types.Special(SpecialType.System_Object);
        var target = new BoundParameterExpression("target", objectType, span);
        var incoming = new BoundParameterExpression("value", objectType, span);
        var receiver = new BoundCastExpression(target, member.ContainingType, span);
        BoundExpression access;
        ITypeSymbol valueType;
        bool writable;
        string name;
        if (member is IPropertySymbol property)
        {
            valueType = property.Type;
            access = new BoundPropertyAccessExpression(receiver, property, indices, span);
            writable = !sourceType.IsValueType && property.SetMethod is { IsInitOnly: false } setter && context.Types.IsAccessible(setter);
            name = property.IsIndexer ? property.MetadataName : property.Name;
        }
        else
        {
            var field = (IFieldSymbol)member;
            valueType = field.Type;
            access = new BoundFieldAccessExpression(receiver, field, span);
            writable = !sourceType.IsValueType && !field.IsReadOnly && !field.IsConst;
            name = field.Name;
        }
        var propertyMethod = context.Types.Find(AvaloniaBindingMetadata.PathBuilder)?.GetMembers("Property").OfType<IMethodSymbol>()
            .FirstOrDefault(m => !m.IsGenericMethod && m.Parameters.Length == 2);
        var infoType = context.Types.Find(AvaloniaBindingMetadata.ClrPropertyInfo);
        var constructor = infoType?.InstanceConstructors.FirstOrDefault(c => c.Parameters.Length == 4);
        if (constructor == null || propertyMethod == null)
        { context.Report("XG3202", "Compiled property accessor contracts are missing.", span); return null; }
        var getterType = (INamedTypeSymbol)constructor.Parameters[1].Type;
        var setterType = (INamedTypeSymbol)constructor.Parameters[2].Type;
        BoundExpression getter = new BoundLambdaExpression(getterType, ImmutableArray.Create(target), access, true, span);
        BoundExpression setterExpression = writable
            ? new BoundLambdaExpression(setterType, ImmutableArray.Create(target, incoming),
                new BoundAssignmentExpression(access, new BoundCastExpression(incoming, valueType, span), span), true, span)
            : new BoundConstantExpression(null, setterType, span);
        BoundExpression info = new BoundNewExpression(constructor,
            ImmutableArray.Create(expressions.Text(name, span), getter, setterExpression, expressions.Type(valueType, span)), span);
        var factoryName = "CreateInpcPropertyAccessor";
        if (indices.IsEmpty && member is IPropertySymbol)
        {
            var registered = sourceType.Members(name + AvaloniaMetadata.PropertySuffix).OfType<IFieldSymbol>()
                .FirstOrDefault(f => f.IsStatic && context.Types.IsAccessible(f) && DerivesRegisteredProperty(f.Type));
            if (registered != null)
            {
                info = new BoundStaticExpression(registered, registered.Type, span);
                factoryName = "CreateAvaloniaPropertyAccessor";
            }
        }
        var factoryType = (INamedTypeSymbol)propertyMethod.Parameters[1].Type;
        var factoryOwner = context.Types.Find(AvaloniaBindingMetadata.AccessorFactory);
        BoundExpression factory;
        if (indices.Length == 1 && indices[0] is BoundConstantExpression { Value: int index })
        {
            var method = factoryOwner?.GetMembers("CreateIndexerPropertyAccessor").OfType<IMethodSymbol>().FirstOrDefault(m => m.Parameters.Length == 3);
            if (method == null) { context.Report("XG3202", "The indexed accessor factory is missing.", span); return null; }
            var invoke = factoryType.DelegateInvokeMethod!;
            var reference = new BoundParameterExpression("reference", invoke.Parameters[0].Type, span);
            var propertyInfo = new BoundParameterExpression("property", invoke.Parameters[1].Type, span);
            factory = new BoundLambdaExpression(factoryType, ImmutableArray.Create(reference, propertyInfo),
                new BoundCallExpression(method, null, ImmutableArray.Create<BoundExpression>(reference, propertyInfo, expressions.Number(index, span)), span), true, span);
        }
        else
        {
            var method = factoryOwner?.GetMembers(factoryName).OfType<IMethodSymbol>().FirstOrDefault(m => m.Parameters.Length == 2);
            if (method == null) { context.Report("XG3202", "The property accessor factory is missing.", span); return null; }
            factory = new BoundMethodGroupExpression(method, null, factoryType, span);
        }
        context.Symbols.Add(new(span, member, indices.IsEmpty ? "binding-member" : "binding-indexer"));
        return new(info, factory, valueType, writable);
    }

    public BindingAccessor? Attached(RegisteredProperty property, TextSpan span)
    {
        var method = context.Types.Find(AvaloniaBindingMetadata.PathBuilder)?.GetMembers("Property").OfType<IMethodSymbol>()
            .FirstOrDefault(m => !m.IsGenericMethod && m.Parameters.Length == 2);
        var factory = context.Types.Find(AvaloniaBindingMetadata.AccessorFactory)?.GetMembers("CreateAvaloniaPropertyAccessor").OfType<IMethodSymbol>().FirstOrDefault();
        if (method == null || factory == null) { context.Report("XG3202", "Attached-property accessor contracts are missing.", span); return null; }
        return new(property.Reference(span), new BoundMethodGroupExpression(factory, null, (INamedTypeSymbol)method.Parameters[1].Type, span), property.ValueType, true);
    }

    private static bool DerivesRegisteredProperty(ITypeSymbol type)
    {
        for (var current = type as INamedTypeSymbol; current != null; current = current.BaseType)
            if (current.OriginalDefinition.HasMetadataName(AvaloniaStyleMetadata.GenericProperty)) return true;
        return false;
    }
}
