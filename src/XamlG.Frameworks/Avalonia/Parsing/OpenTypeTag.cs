using System;

namespace XamlG.Frameworks.Avalonia.Parsing
{
    internal readonly record struct OpenTypeTag
    {
        internal static readonly OpenTypeTag None = new OpenTypeTag(0, 0, 0, 0);
        internal static readonly OpenTypeTag Max = new OpenTypeTag(byte.MaxValue, byte.MaxValue, byte.MaxValue, byte.MaxValue);
        internal static readonly OpenTypeTag MaxSigned = new OpenTypeTag((byte)sbyte.MaxValue, byte.MaxValue, byte.MaxValue, byte.MaxValue);

        private readonly uint _value;

        public OpenTypeTag(uint value)
        {
            _value = value;
        }

        public OpenTypeTag(char c1, char c2, char c3, char c4)
        {
            _value = (uint)(((byte)c1 << 24) | ((byte)c2 << 16) | ((byte)c3 << 8) | (byte)c4);
        }

        private OpenTypeTag(byte c1, byte c2, byte c3, byte c4)
        {
            _value = (uint)((c1 << 24) | (c2 << 16) | (c3 << 8) | c4);
        }

        public static OpenTypeTag Parse(string tag) => Parse(tag.AsSpan());

        public static OpenTypeTag Parse(ReadOnlySpan<char> tag)
        {
            if (tag.IsEmpty) return None;
            return new OpenTypeTag(tag[0], tag.Length > 1 ? tag[1] : ' ',
                tag.Length > 2 ? tag[2] : ' ', tag.Length > 3 ? tag[3] : ' ');
        }

        public override string ToString()
        {
            if (_value == None)
            {
                return nameof(None);
            }
            if (_value == Max)
            {
                return nameof(Max);
            }
            if (_value == MaxSigned)
            {
                return nameof(MaxSigned);
            }

            return string.Concat(
                (char)(byte)(_value >> 24),
                (char)(byte)(_value >> 16),
                (char)(byte)(_value >> 8),
                (char)(byte)_value);
        }

        public static implicit operator uint(OpenTypeTag tag) => tag._value;

        public static implicit operator OpenTypeTag(uint tag) => new OpenTypeTag(tag);
    }
}
