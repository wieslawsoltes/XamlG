using System;

namespace XamlG.Frameworks.Avalonia.Parsing
{
    // Numeric helpers retained from upstream for the color and transform parsers.
    internal static class MathUtilities
    {
        public static double Deg2Rad(double angle)
        {
            return angle * (Math.PI / 180d);
        }

        public static double Grad2Rad(double angle)
        {
            return angle * (Math.PI / 200d);
        }

        public static double Turn2Rad(double angle)
        {
            return angle * 2 * Math.PI;
        }

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
