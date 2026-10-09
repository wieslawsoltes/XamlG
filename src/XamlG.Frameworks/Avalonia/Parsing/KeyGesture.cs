using System;

namespace XamlG.Frameworks.Avalonia.Parsing
{
    /// <summary>
    /// Defines a keyboard input combination.
    /// </summary>
    internal sealed class KeyGesture : IEquatable<KeyGesture>
    {
        public KeyGesture(Key key, KeyModifiers modifiers = KeyModifiers.None)
        {
            Key = key;
            KeyModifiers = modifiers;
        }

        public bool Equals(KeyGesture? other)
        {
            if (ReferenceEquals(null, other)) return false;
            if (ReferenceEquals(this, other)) return true;

            return Key == other.Key && KeyModifiers == other.KeyModifiers;
        }

        public override bool Equals(object? obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            if (ReferenceEquals(this, obj)) return true;

            return obj is KeyGesture gesture && Equals(gesture);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return ((int)Key * 397) ^ (int)KeyModifiers;
            }
        }

        public static bool operator ==(KeyGesture? left, KeyGesture? right)
        {
            return Equals(left, right);
        }

        public static bool operator !=(KeyGesture? left, KeyGesture? right)
        {
            return !Equals(left, right);
        }

        public Key Key { get; }
        
        public KeyModifiers KeyModifiers { get; }

        public static KeyGesture Parse(string gesture)
        {
            // string.Split can't be used here because "Ctrl++" is a perfectly valid key gesture

            var key = Key.None;
            var keyModifiers = KeyModifiers.None;

            var cstart = 0;

            for (var c = 0; c <= gesture.Length; c++)
            {
                var ch = c == gesture.Length ? '\0' : gesture[c];
                bool isLast = c == gesture.Length;

                if (isLast || (ch == '+' && cstart != c))
                {
                    var partSpan = gesture.AsSpan(cstart, c - cstart).Trim();

                    if (!TryParseKey(partSpan, out key))
                    {
                        keyModifiers |= ParseModifier(partSpan);
                    }
                    cstart = c + 1;
                }
            }


            return new KeyGesture(key, keyModifiers);
        }

        // TODO: Move that to external key parser
        private static bool TryParseKey(ReadOnlySpan<char> keyStr, out Key key)
        {
            key = Key.None;
            if (keyStr.Length == 1)
            {
                key = keyStr[0] switch { '+' => Key.OemPlus, '-' => Key.OemMinus, '.' => Key.OemPeriod, ',' => Key.OemComma, _ => Key.None };
                if (key != Key.None) return true;
            }
            if (SpanHelpers.TryParseEnum(keyStr, true, out key))
                return true;

            return false;
        }

        private static KeyModifiers ParseModifier(ReadOnlySpan<char> modifier)
        {
            if (modifier.Equals("ctrl".AsSpan(), StringComparison.OrdinalIgnoreCase))
            {
                return KeyModifiers.Control;
            }

            if (modifier.Equals("cmd".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
                modifier.Equals("win".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
                modifier.Equals("⌘".AsSpan(), StringComparison.OrdinalIgnoreCase))
            {
                return KeyModifiers.Meta;
            }

            if (SpanHelpers.TryParseEnum<KeyModifiers>(modifier, true, out var value)) return value;
            return (KeyModifiers)Enum.Parse(typeof(KeyModifiers), modifier.ToString(), true);
        }

    }
}
