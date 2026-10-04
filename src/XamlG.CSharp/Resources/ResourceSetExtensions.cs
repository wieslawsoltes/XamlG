namespace XamlG.CSharp.Resources;

/// <summary>Local .NET Standard 2.0 collection construction; newer LINQ APIs are not part of the compiler ABI.</summary>
internal static class ResourceSetExtensions
{
    public static HashSet<T> ToHashSet<T>(this IEnumerable<T> source) => new(source);
    public static HashSet<T> ToHashSet<T>(this IEnumerable<T> source, IEqualityComparer<T> comparer) => new(source, comparer);
}
