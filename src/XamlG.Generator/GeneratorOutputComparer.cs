namespace XamlG.Generator;

internal sealed class GeneratorOutputComparer : IEqualityComparer<GeneratorOutput>
{
    public static GeneratorOutputComparer Instance { get; } = new();
    public bool Equals(GeneratorOutput? x, GeneratorOutput? y) => ReferenceEquals(x, y) ||
        x != null && y != null && x.Path == y.Path && x.Text == y.Text && x.HintName == y.HintName &&
        x.Source == y.Source && x.Diagnostics.SequenceEqual(y.Diagnostics) && x.HostDiagnostics.SequenceEqual(y.HostDiagnostics);
    public int GetHashCode(GeneratorOutput value)
    {
        unchecked
        {
            var hash = StringComparer.Ordinal.GetHashCode(value.Path);
            hash = hash * 397 ^ StringComparer.Ordinal.GetHashCode(value.Source);
            foreach (var diagnostic in value.Diagnostics) hash = hash * 397 ^ diagnostic.GetHashCode();
            foreach (var diagnostic in value.HostDiagnostics) hash = hash * 397 ^ diagnostic.GetHashCode();
            return hash;
        }
    }
}
