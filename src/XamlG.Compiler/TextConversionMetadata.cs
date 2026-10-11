using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Threading;
using Microsoft.CodeAnalysis;
using XamlG.Internal;
using XamlG.Roslyn;

namespace XamlG.Compiler;

/// <summary>Pure conversion metadata, weakly owned by the exact immutable type system
/// and symbol identities. Never cache rule/provider calls, text, bound expressions,
/// diagnostics, source scopes or document contexts.</summary>
internal static class TextConversionMetadata
{
    private static readonly ConditionalWeakTable<RoslynTypeSystem, Cache> Caches = new();
    private sealed class Cache
    {
        public readonly ConditionalWeakTable<ITypeSymbol, ParseSelection> Parses = new();
        public readonly ConditionalWeakTable<ISymbol, ConverterSelection> Converters = new();
    }
    private sealed record ParseSelection(IMethodSymbol? Method);
    private sealed record ConverterSelection(INamedTypeSymbol? Type);

    public static IMethodSymbol? FindParse(RoslynTypeSystem types, ITypeSymbol target, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var cache = Caches.GetValue(types, static _ => new());
        if (!cache.Parses.TryGetValue(target, out var selected))
            selected = ReadParse(cache, types, target, cancellation);
        cancellation.ThrowIfCancellationRequested();
        return selected.Method;
    }

    private static ParseSelection ReadParse(Cache cache, RoslynTypeSystem types, ITypeSymbol target, CancellationToken cancellation)
    {
        IMethodSymbol? best = null;
        foreach (var candidate in target.Members(ClrNames.Parse))
        {
            cancellation.ThrowIfCancellationRequested();
            if (candidate is not IMethodSymbol method || !method.IsStatic || method.IsGenericMethod ||
                !types.IsAccessible(method) || method.Parameters.Length is not (1 or 2) ||
                method.Parameters[0].Type.SpecialType != SpecialType.System_String ||
                method.Parameters.Length == 2 && !method.Parameters[1].Type.HasMetadataName(ClrNames.IFormatProvider) &&
                    !method.Parameters[1].Type.HasMetadataName(ClrNames.CultureInfo) ||
                !types.Compilation.ClassifyCommonConversion(method.ReturnType, target).IsImplicit) continue;
            // Stable descending arity: first eligible two-argument overload wins,
            // otherwise first one-argument overload. Preserve inherited member order.
            if (best == null || method.Parameters.Length > best.Parameters.Length) best = method;
        }
        var result = new ParseSelection(best);
        cancellation.ThrowIfCancellationRequested();
        // Competing readers may compute metadata, but publish one immutable entry.
        // The callback closes over the result only, never a source-bearing context.
        return cache.Parses.GetValue(target, _ => result);
    }

    public static INamedTypeSymbol? FindAttributeConverter(RoslynTypeSystem types, ISymbol symbol, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var attributes = symbol.GetAttributes();
        // Most ordinary properties have no attributes. Do not create cache owners,
        // keys or negative-result wrappers for this already allocation-free case.
        if (attributes.IsEmpty || types.Configuration.TypeConverterAttributes.IsEmpty) return null;
        var cache = Caches.GetValue(types, static _ => new());
        if (!cache.Converters.TryGetValue(symbol, out var selected))
            selected = ReadConverter(cache, types, symbol, attributes, cancellation);
        cancellation.ThrowIfCancellationRequested();
        return selected.Type;
    }

    private static ConverterSelection ReadConverter(Cache cache, RoslynTypeSystem types, ISymbol symbol,
        ImmutableArray<AttributeData> attributes, CancellationToken cancellation)
    {
        INamedTypeSymbol? result = null;
        foreach (var attribute in attributes)
        {
            cancellation.ThrowIfCancellationRequested();
            if (attribute.AttributeClass == null ||
                !types.Configuration.TypeConverterAttributes.Contains(attribute.AttributeClass.MetadataName())) continue;
            var value = attribute.ConstructorArguments.Length == 0 ? null : attribute.ConstructorArguments[0].Value;
            var type = value as INamedTypeSymbol;
            if (type == null && value is string name)
            {
                var parts = new SpanSplitEnumerator(name.AsSpan(), ',');
                parts.MoveNext();
                type = types.Find(parts.Current.Trim().ToString());
            }
            if (type != null && IsConverter(types, type)) { result = type; break; }
        }
        var selected = new ConverterSelection(result);
        cancellation.ThrowIfCancellationRequested();
        return cache.Converters.GetValue(symbol, _ => selected);
    }

    // Provider results deliberately pass through this validation on every call.
    // Only immutable attribute-derived selection is memoized above.
    public static bool IsConverter(RoslynTypeSystem types, INamedTypeSymbol type)
    {
        if (!types.IsAccessible(type)) return false;
        var constructible = false;
        foreach (var constructor in type.InstanceConstructors)
            if (constructor.Parameters.Length == 0 && types.IsAccessible(constructor)) { constructible = true; break; }
        if (!constructible) return false;
        for (var current = type; current != null; current = current.BaseType)
            if (current.HasMetadataName(ClrNames.TypeConverter)) return true;
        return false;
    }
}
