using System.Security.Cryptography;
using XamlG.Syntax;

internal static class WhitespaceBufferProbe
{
    public static string Run(Action<string, int, Func<int>> measure)
    {
        using var signatures = new MemoryStream();
        using (var writer = new BinaryWriter(signatures, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            foreach (var length in new[] { 255, 256, 257, 16383, 16384, 16385, 65536, 131073 })
            foreach (var shape in new[] { "dense", "late", "boundary" })
            {
                var characters = new char[length];
                if (shape == "dense")
                {
                    const string pattern = "ab\t\r\n c\u00a0\ud800\udfff";
                    for (var i = 0; i < length; i++) characters[i] = pattern[i % pattern.Length];
                }
                else
                {
                    Array.Fill(characters, 'x');
                    if (shape == "late") characters[length - 2] = '\t';
                    else { characters[0] = '\t'; characters[length - 1] = '\r'; }
                }
                var text = new string(characters);
                // Setup and raw UTF-16 output checks are outside timing. Retain all
                // trim modes, including non-XML whitespace and isolated surrogates.
                writer.Write(length); writer.Write(shape);
                for (var mask = 0; mask < 4; mask++)
                {
                    var result = XmlWhitespace.Collapse(text, (mask & 1) != 0, (mask & 2) != 0);
                    if (result.Length > text.Length) throw new InvalidOperationException("Whitespace expanded source length.");
                    writer.Write(mask); writer.Write(result.Length);
                    foreach (var character in result) writer.Write((ushort)character);
                }
                measure("whitespace-buffer-" + shape + "-" + length, Math.Max(1, 32768 / length),
                    () => XmlWhitespace.Collapse(text, true, true).Length);
            }
        }
        // The shared harness uses warmed fresh processes, not guaranteed cold pool
        // rents. Allocated bytes are not total retained-pool/process-heap measurements.
        return Convert.ToHexString(SHA256.HashData(signatures.ToArray()));
    }
}
