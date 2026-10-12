using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using XamlG.Compiler;
using XamlG.CSharp;
using XamlG.Runtime;
using XamlG.Syntax;

internal sealed record DecodedMapStorage(string Name, int RawLength, int DecodedLength, int BoundaryElements, int ElementBytes);
internal sealed record DecodedMapReport(string SemanticSignature, List<DecodedMapStorage> Storage);

internal static class DecodedSourceMapProbe
{
    public static DecodedMapReport Run(Action<string, int, Func<int>> measure)
    {
        var storageField = typeof(XamlDecodedTextMap).GetField("_boundaries", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var storage = new List<DecodedMapStorage>();
        using var signature = new MemoryStream();
        using (var writer = new BinaryWriter(signature, Encoding.UTF8, leaveOpen: true))
        {
            foreach (var length in new[] { 32, 4096, 65536 })
            foreach (var shape in new[] { "plain", "normalized-identity", "sparse-bmp", "sparse-pair", "sparse-crlf", "dense-bmp", "dense-pair", "late-dense" })
            {
                var raw = shape switch
                {
                    "plain" => new string('x', length),
                    "normalized-identity" => new string('x', length / 2) + "\t\r" + new string('y', length / 2),
                    "sparse-bmp" => new string('x', length / 2) + "&amp;" + new string('y', length / 2),
                    "sparse-pair" => new string('x', length / 2) + "&#x1F600;" + new string('y', length / 2),
                    "sparse-crlf" => new string('x', length / 2) + "\r\n" + new string('y', length / 2),
                    "dense-bmp" => string.Concat(Enumerable.Repeat("&amp;", Math.Max(1, length / 5))),
                    "dense-pair" => string.Concat(Enumerable.Repeat("&#x1F600;", Math.Max(1, length / 9))),
                    _ => new string('x', 129) + string.Concat(Enumerable.Repeat("&amp;&#x10000;\r\n", Math.Max(1, length / 16)))
                };
                var name = shape + "-" + length;
                var source = "prefix:" + raw + ":suffix";
                var range = new TextSpan(7, raw.Length);
                var map = XamlDecodedTextMap.Create(source, range);
                measure("decoded-map-create-" + name, length <= 4096 ? 20 : 3, () =>
                {
                    var value = XamlDecodedTextMap.Create(source, range);
                    GC.KeepAlive(value);
                    return value.Text.Length;
                });
                var queries = new TextSpan[32];
                for (var i = 0; i < queries.Length; i++)
                {
                    var position = (int)((long)map.Text.Length * i / (queries.Length - 1));
                    try { map.ToSource(new(position, 0)); }
                    catch (ArgumentException) { position--; }
                    queries[i] = new(position, 0);
                }
                measure("decoded-map-query32-" + name, 1000, () =>
                {
                    var checksum = 0;
                    foreach (var query in queries) checksum = unchecked(checksum + map.ToSource(query).Start);
                    return checksum;
                });
                var data = (Array?)storageField.GetValue(map);
                storage.Add(new(name, raw.Length, map.Text.Length, data?.Length ?? 0,
                    data == null ? 0 : Marshal.SizeOf(data.GetType().GetElementType()!)));
                writer.Write(name);
                // Store raw UTF-16 rather than UTF-8 replacement of surrogate halves.
                writer.Write(map.Text.Length);
                foreach (var c in map.Text) writer.Write((ushort)c);
                for (var position = 0; position <= map.Text.Length; position++)
                {
                    try
                    {
                        var point = map.ToSource(new(position, 0));
                        writer.Write(point.Start);
                        writer.Write(map.ToSource(new(0, position)).Length);
                        writer.Write(map.ToSource(new(position, map.Text.Length - position)).Length);
                    }
                    catch (ArgumentException error) { writer.Write(-1); writer.Write(error.ParamName ?? ""); }
                }
            }
            CompileEncodedMarkup(measure, writer);
        }
        return new(Convert.ToHexString(SHA256.HashData(signature.ToArray())), storage);
    }

    private static void CompileEncodedMarkup(Action<string, int, Func<int>> measure, BinaryWriter writer)
    {
        const string model = """
            using System;
            namespace DecodedMapFixture {
                public class Root { public string Text { get; set; } = ""; }
                public class EchoExtension {
                    public string Text { get; set; } = "";
                    public string ProvideValue(IServiceProvider provider) => Text;
                }
            }
            """;
        var paths = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Append(typeof(XamlRuntimeContext).Assembly.Location).Distinct(StringComparer.Ordinal);
        var options = new CSharpParseOptions(LanguageVersion.Preview);
        var compilation = CSharpCompilation.Create("DecodedMapFixture", new[] { CSharpSyntaxTree.ParseText(model, options) },
            paths.Select(path => MetadataReference.CreateFromFile(path)),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        foreach (var length in new[] { 32, 4096 })
        {
            var source = "<Root xmlns='clr-namespace:DecodedMapFixture' Text='{Echo Text=&quot;" +
                new string('x', length) + "&amp;&#x1F600;&quot;}'/>";
            var syntax = XamlSyntaxTree.Parse(source, "Encoded.xaml");
            var bound = new XamlCompiler().Bind(syntax, compilation, XamlFrameworkProfile.Portable);
            if (!bound.Success) throw new InvalidOperationException(string.Join("\n", bound.Diagnostics));
            var output = new CSharpEmitter().Emit(bound);
            if (!output.Success) throw new InvalidOperationException(string.Join("\n", output.Diagnostics));
            using var stream = new MemoryStream();
            var emitted = compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(output.Source, options)).Emit(stream);
            if (!emitted.Success) throw new InvalidOperationException(string.Join("\n", emitted.Diagnostics));
            writer.Write(output.Source);
            writer.Write(System.Text.Json.JsonSerializer.Serialize(output.SourceMappings));
            writer.Write(System.Text.Json.JsonSerializer.Serialize(output.Diagnostics));
            measure("bind-encoded-source-map-" + length, 20, () =>
                new XamlCompiler().Bind(syntax, compilation, XamlFrameworkProfile.Portable).Diagnostics.Length);
        }
    }
}
