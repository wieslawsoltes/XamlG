namespace XamlG.Runtime.Design;

public readonly record struct XamlDesignRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public bool IsValid => Finite(X) && Finite(Y) && Finite(Width) && Finite(Height) && Width >= 0 && Height >= 0;
    internal static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
}
