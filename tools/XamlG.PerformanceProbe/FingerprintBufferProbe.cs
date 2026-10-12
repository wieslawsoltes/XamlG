using System.Linq.Expressions;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using XamlG.Compiler;
using XamlG.CSharp;
using XamlG.Runtime;
using XamlG.Syntax;

internal static class FingerprintBufferProbe
{
    internal sealed record Scratch(string Name, int BufferBytes, int DisposedBufferBytes);
    internal sealed record Result(string Signature, IReadOnlyList<Scratch> Storage);

    public static Result Run(Action<string, int, Func<int>> measure)
    {
        const string model = """
            using System.Collections.Generic;
            using XamlG.Runtime;
            namespace FingerprintProbeModel {
                public class Root { [Content] public List<Item> Children { get; } = new(); }
                public class Item { public string Text { get; set; } = ""; }
            }
            """;
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        var paths = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Append(typeof(XamlRuntimeContext).Assembly.Location).Distinct(StringComparer.Ordinal);
        var compilation = CSharpCompilation.Create("FingerprintProbeModel", [CSharpSyntaxTree.ParseText(model, parseOptions)],
            paths.Select(path => MetadataReference.CreateFromFile(path)), new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var document = new XamlCompiler().Bind(XamlSyntaxTree.Parse("<Root xmlns='clr-namespace:FingerprintProbeModel'/>", "Hash.xaml"), compilation);
        Require(document.Success, "Fingerprint fixture binding failed");
        var contextType = typeof(CSharpEmitter).Assembly.GetType("XamlG.CSharp.EmissionContext", true)!;
        var constructor = contextType.GetConstructor([typeof(BoundDocument), typeof(CancellationToken)])!;
        var method = contextType.GetMethod("StableId", [typeof(string), typeof(string), typeof(TextSpan)])!;
        var buffer = contextType.GetField("_hashInput", BindingFlags.Instance | BindingFlags.NonPublic)!;
        // Build open delegates once. Reflection and delegate compilation are never timed.
        var doc = Expression.Parameter(typeof(BoundDocument));
        var create = Expression.Lambda<Func<BoundDocument, object>>(Expression.Convert(
            Expression.New(constructor, doc, Expression.Constant(CancellationToken.None)), typeof(object)), doc).Compile();
        var owner = Expression.Parameter(typeof(object));
        var prefixArg = Expression.Parameter(typeof(string));
        var textArg = Expression.Parameter(typeof(string));
        var spanArg = Expression.Parameter(typeof(TextSpan));
        var hash = Expression.Lambda<Func<object, string?, string, TextSpan, string>>(
            Expression.Call(Expression.Convert(owner, contextType), method, prefixArg, textArg, spanArg), owner, prefixArg, textArg, spanArg).Compile();
        var signature = new StringBuilder();
        var storage = new List<Scratch>();

        foreach (var length in new[] { 32, 4096, 5460, 5461, 65536, 1048576 })
        foreach (var unicode in new[] { false, true })
        {
            var text = Text(length, unicode);
            var span = new TextSpan(0, text.Length);
            var shape = unicode ? "unicode" : "ascii";
            var name = "fingerprint-warm-" + shape + "-" + length;
            var context = create(document);
            using (var owned = (IDisposable)context)
            {
                var digest = hash(context, null, text, span);
                Require(digest == Previous(null, text, span), "Fingerprint bytes changed");
                signature.Append(name).Append(':').Append(digest).Append('\n');
                measure(name, length >= 65536 ? 3 : 100, () => hash(context, null, text, span).Length);
                Record(name, context);
            }
            if (length is 5460 or 5461) continue;
            name = "fingerprint-cold-" + shape + "-" + length;
            measure(name, length >= 65536 ? 3 : 30, () =>
            {
                var cold = create(document);
                using var owned = (IDisposable)cold;
                return hash(cold, null, text, span).Length;
            });
        }

        foreach (var count in new[] { 64, 512 })
        foreach (var descending in new[] { false, true })
        {
            var text = new string('x', count * 16);
            var spans = Enumerable.Range(1, count).Select(i => new TextSpan(0, i * 16)).ToArray();
            if (descending) Array.Reverse(spans);
            var name = "fingerprint-growing-" + (descending ? "descending-" : "ascending-") + count;
            measure(name, count == 64 ? 8 : 2, () =>
            {
                var context = create(document);
                using var owned = (IDisposable)context;
                var checksum = 0;
                foreach (var span in spans) checksum ^= hash(context, "type", text, span)[0];
                return checksum;
            });
            var check = create(document);
            using (var owned = (IDisposable)check)
            {
                foreach (var span in spans)
                {
                    var digest = hash(check, "type", text, span);
                    Require(digest == Previous("type", text, span), "Growth sequence fingerprint changed");
                    signature.Append(digest);
                }
                Record(name, check);
            }
        }

        foreach (var unicode in new[] { false, true })
        {
            var prefix = Text(65536, unicode);
            const string text = "small-value";
            var name = "fingerprint-large-prefix-" + (unicode ? "unicode" : "ascii");
            var context = create(document);
            using var owned = (IDisposable)context;
            var span = new TextSpan(0, text.Length);
            var digest = hash(context, prefix, text, span);
            Require(digest == Previous(prefix, text, span), "Large prefix changed");
            signature.Append(name).Append(':').Append(digest).Append('\n');
            measure(name, 10, () => hash(context, prefix, text, span).Length);
            Record(name, context);
        }

        foreach (var count in new[] { 32, 512 })
        foreach (var width in new[] { 64, 1024 })
        {
            var source = "<Root xmlns='clr-namespace:FingerprintProbeModel'>\n" +
                string.Concat(Enumerable.Repeat("<Item Text='" + new string('x', width) + "'/>\n", count)) + "</Root>";
            var bound = new XamlCompiler().Bind(XamlSyntaxTree.Parse(source, "Fingerprint.xaml"), compilation);
            Require(bound.Success, "Complete fingerprint fixture failed to bind");
            var output = new CSharpEmitter().Emit(bound);
            Require(output.Success, "Complete fingerprint fixture failed to emit");
            using (var stream = new MemoryStream())
            {
                var result = compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(output.Source, parseOptions)).Emit(stream);
                Require(result.Success, "Generated fingerprint fixture failed to compile: " + string.Join("\n", result.Diagnostics));
            }
            var name = "fingerprint-emission-" + count + "-text" + width;
            measure(name, count == 32 ? 4 : 1, () => new CSharpEmitter().Emit(bound).Source.Length);
            signature.Append(name).Append('\n').Append(output.Source)
                .Append(JsonSerializer.Serialize(output.SourceMappings)).Append(JsonSerializer.Serialize(output.Diagnostics));
        }
        return new(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(signature.ToString()))), storage);

        void Record(string name, object context)
        {
            var bytes = ((byte[])buffer.GetValue(context)!).Length;
            ((IDisposable)context).Dispose();
            var disposed = ((byte[])buffer.GetValue(context)!).Length;
            storage.Add(new(name, bytes, disposed));
            Console.WriteLine($"Fingerprint scratch {name}: live={bytes} bytes; disposed={disposed} bytes");
        }
    }

    private static string Text(int length, bool unicode)
    {
        if (!unicode) return new('x', length);
        const string pattern = "\u6f22\ud83d\ude80";
        var chars = new char[length];
        for (var i = 0; i < chars.Length; i++) chars[i] = pattern[i % pattern.Length];
        // Keep benchmark text well formed; malformed UTF-16 has separate native coverage.
        if (chars.Length != 0 && char.IsHighSurrogate(chars[chars.Length - 1])) chars[chars.Length - 1] = '\u6f22';
        return new(chars);
    }

    private static string Previous(string? prefix, string text, TextSpan span)
    {
        var encoding = Encoding.UTF8;
        var bytes = new byte[(prefix == null ? 0 : encoding.GetByteCount(prefix) + 1) + encoding.GetMaxByteCount(span.Length)];
        var offset = prefix == null ? 0 : encoding.GetBytes(prefix, 0, prefix.Length, bytes, 0);
        if (prefix != null) bytes[offset++] = 0;
        offset += encoding.GetBytes(text, span.Start, span.Length, bytes, offset);
        return Convert.ToHexString(SHA256.HashData(bytes.AsSpan(0, offset)), 0, 12).ToLowerInvariant();
    }

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
