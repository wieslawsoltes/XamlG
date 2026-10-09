using System;
using System.Globalization;

namespace XamlG.Frameworks.Avalonia.Parsing
{
    /// <summary>
    /// Parses easing literals using compiler-provided typed construction callbacks.
    /// </summary>
    internal static class Easing
    {
        private const string Namespace = "Avalonia.Animation.Easings";

        /// <summary>
        /// Parses a Easing type string.
        /// </summary>
        /// <param name="e">The Easing type string.</param>
        /// <returns>Returns the instance of the parsed type.</returns>
        public static T Parse<T>(string e, Func<string, T?> createNamed, Func<KeySpline, T> createSpline) where T : class
        {
#if NETSTANDARD2_0
            if (e.Contains(","))
#else
            if (e.Contains(','))
#endif
            {
                return createSpline(KeySpline.Parse(e, CultureInfo.InvariantCulture));
            }

            return createNamed(e) ?? throw new FormatException($"Easing \"{e}\" was not found in {Namespace} namespace.");
        }
    }
}
