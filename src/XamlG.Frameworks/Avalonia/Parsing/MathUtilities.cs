using System;

namespace XamlG.Frameworks.Avalonia.Parsing
{
    // Only the numeric clamp used by the imported color parsers is needed here.
    internal static class MathUtilities
    {
        public static double Clamp(double val, double min, double max)
        {
            if (min > max)
            {
                ThrowCannotBeGreaterThanException(min, max);
            }

            if (val < min)
            {
                return min;
            }
            else if (val > max)
            {
                return max;
            }
            else
            {
                return val;
            }
        }

        private static void ThrowCannotBeGreaterThanException<T>(T min, T max)
        {
            throw new ArgumentException($"{min} cannot be greater than {max}.");
        }
    }
}
