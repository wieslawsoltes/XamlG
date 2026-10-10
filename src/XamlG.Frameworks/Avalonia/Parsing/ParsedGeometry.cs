namespace XamlG.Frameworks.Avalonia.Parsing;

internal readonly record struct Point(double X, double Y)
{
    public Point WithX(double x) => new(x, Y);
    public Point WithY(double y) => new(X, y);
    public static Point operator +(Point left, Point right) => new(left.X + right.X, left.Y + right.Y);
    public static Point operator -(Point left, Point right) => new(left.X - right.X, left.Y - right.Y);
    public static Point operator -(Point value) => new(-value.X, -value.Y);
}

internal readonly record struct Size(double Width, double Height);
internal enum FillRule { EvenOdd, NonZero }
internal enum SweepDirection { CounterClockwise, Clockwise }
internal sealed record ParsedGeometryCall(string Method, object[] Arguments);

/// <summary>Records the upstream parser's calls without creating platform geometry or loading Avalonia.</summary>
internal sealed class ParsedGeometry : IGeometryContext
{
    public List<ParsedGeometryCall> Calls { get; } = new();
    public void SetFillRule(FillRule fillRule) => Add(nameof(SetFillRule), fillRule);
    public void BeginFigure(Point startPoint, bool isFilled = true) => Add(nameof(BeginFigure), startPoint, isFilled);
    public void EndFigure(bool isClosed) => Add(nameof(EndFigure), isClosed);
    public void LineTo(Point point, bool isStroked = true) => Add(nameof(LineTo), point, isStroked);
    public void QuadraticBezierTo(Point controlPoint, Point endPoint, bool isStroked = true) =>
        Add(nameof(QuadraticBezierTo), controlPoint, endPoint, isStroked);
    public void CubicBezierTo(Point controlPoint1, Point controlPoint2, Point endPoint, bool isStroked = true) =>
        Add(nameof(CubicBezierTo), controlPoint1, controlPoint2, endPoint, isStroked);
    public void ArcTo(Point point, Size size, double rotationAngle, bool isLargeArc, SweepDirection sweepDirection, bool isStroked = true) =>
        Add(nameof(ArcTo), point, size, rotationAngle, isLargeArc, sweepDirection, isStroked);
    public void Dispose() { }
    private void Add(string method, params object[] arguments) => Calls.Add(new(method, arguments));
}
