using System;

namespace XamlG.Frameworks.Avalonia.Parsing;

internal static class SpanEnumParser
{
    public static bool TryParse<TEnum>(ReadOnlySpan<char> text, bool ignoreCase, out TEnum value) where TEnum : struct
    {
        if (typeof(TEnum).IsEnum)
        {
            var token = text.Trim();
            var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            foreach (var entry in Names<TEnum>.Entries)
                if (token.Equals(entry.Name.AsSpan(), comparison)) { value = entry.Value; return true; }
        }
        // netstandard2.0 has no Enum.TryParse span overload. Keep the BCL's
        // numeric, flags, overflow and error semantics for uncommon forms.
        // Named tokens, the common parser path, never materialize a string.
        return Enum.TryParse(text.ToString(), ignoreCase, out value);
    }

    private static class Names<TEnum> where TEnum : struct
    {
        public static readonly (string Name, TEnum Value)[] Entries = Create();
        private static (string Name, TEnum Value)[] Create()
        {
            var names = Enum.GetNames(typeof(TEnum));
            var entries = new (string, TEnum)[names.Length];
            for (var i = 0; i < names.Length; i++)
            {
                Enum.TryParse<TEnum>(names[i], out var value);
                entries[i] = (names[i], value);
            }
            return entries;
        }
    }
}
