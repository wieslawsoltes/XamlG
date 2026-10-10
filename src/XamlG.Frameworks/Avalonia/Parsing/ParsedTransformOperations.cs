namespace XamlG.Frameworks.Avalonia.Parsing;

// Record the upstream parser's ordered builder calls without loading the target
// framework or baking matrices. The application invokes its own typed builder.
internal sealed record ParsedTransformOperations(IReadOnlyList<ParsedTransformOperation> Operations)
{
    public static ParsedTransformOperations Identity { get; } = new(Array.Empty<ParsedTransformOperation>());
    public static Builder CreateBuilder(int capacity) => new(capacity);

    internal sealed class Builder(int capacity)
    {
        private readonly List<ParsedTransformOperation> _operations = new(capacity);
        public void AppendTranslate(double x, double y) => Add("AppendTranslate", x, y);
        public void AppendRotate(double angle) => Add("AppendRotate", angle);
        public void AppendScale(double x, double y) => Add("AppendScale", x, y);
        public void AppendSkew(double x, double y) => Add("AppendSkew", x, y);
        public void AppendMatrix(ParsedTransformMatrix matrix) =>
            Add("AppendMatrix", matrix.M11, matrix.M12, matrix.M21, matrix.M22, matrix.M31, matrix.M32);
        public ParsedTransformOperations Build() => new(_operations);
        private void Add(string method, params double[] arguments) => _operations.Add(new(method, arguments));
    }
}

internal sealed record ParsedTransformOperation(string Method, double[] Arguments);
internal readonly record struct ParsedTransformMatrix(double M11, double M12, double M21, double M22, double M31, double M32);
