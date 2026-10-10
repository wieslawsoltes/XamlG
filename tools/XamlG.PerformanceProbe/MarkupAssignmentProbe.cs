using System.Collections;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using XamlG.Compiler;
using XamlG.CSharp;
using XamlG.Runtime;
using XamlG.Syntax;

internal sealed record MarkupProbeReport(int Count, int SourceBytes, int MethodBodies, int IlBytes, int LocalSlots,
    int ExceptionRegions, string SemanticSignature);

internal static class MarkupAssignmentProbe
{
    public static List<MarkupProbeReport> Run(Action<string, int, Func<int>> measure)
    {
        const string model = """
            using System;
            using System.Collections.Generic;
            using XamlG.Runtime;
            namespace MarkupProbeModel;
            public class Root { [Content] public List<Item> Children { get; } = new(); }
            public class Item { public object Value { get; set; } }
            public class EchoExtension
            {
                public string Value { get; }
                public string Extra { get; set; }
                public EchoExtension(string value) { Value = value; }
                public object ProvideValue(IServiceProvider provider)
                {
                    var frame = (XamlRuntimeContext)provider.GetService(typeof(XamlRuntimeContext));
                    if (frame.TargetObject is not Item || !Equals(frame.TargetProperty, "Value") ||
                        frame.RootObject is not Root || frame.Session.FindNode(this) == null)
                        throw new InvalidOperationException("Incorrect markup target services");
                    return this;
                }
            }
            """;
        var paths = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Append(typeof(XamlRuntimeContext).Assembly.Location).Distinct(StringComparer.Ordinal);
        var references = paths.Select(path => MetadataReference.CreateFromFile(path)).ToArray();
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        var compilation = CSharpCompilation.Create("MarkupProbe", new[] { CSharpSyntaxTree.ParseText(model, parseOptions) }, references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release, allowUnsafe: true));
        var profile = XamlFrameworkProfile.Portable with { Runtime = new() { TargetPropertyMode = XamlTargetPropertyMode.Name } };
        var reports = new List<MarkupProbeReport>();
        foreach (var count in new[] { 100, 1000 })
        {
            var xaml = "<Root xmlns='clr-namespace:MarkupProbeModel'>" + string.Concat(Enumerable.Range(0, count)
                .Select(i => "<Item Value='{Echo value" + i + ", Extra=extra" + i + "}'/>")) + "</Root>";
            var bound = new XamlCompiler().Bind(XamlSyntaxTree.Parse(xaml, "Markup.xaml"), compilation, profile);
            if (!bound.Success) throw new InvalidOperationException(string.Join("\n", bound.Diagnostics));
            var output = new CSharpEmitter().Emit(bound);
            if (!output.Success) throw new InvalidOperationException(string.Join("\n", output.Diagnostics));
            measure("emit-markup-" + count, 2, () => new CSharpEmitter().Emit(bound).Source.Length);

            // Fresh generated syntax and compilation for each operation. No analyzer
            // driver is loaded here; full-catalog CI measures normal analyzers separately.
            byte[] Compile()
            {
                var generated = CSharpSyntaxTree.ParseText(output.Source, parseOptions, output.HintName);
                using var stream = new MemoryStream();
                var result = compilation.AddSyntaxTrees(generated).Emit(stream);
                if (!result.Success) throw new InvalidOperationException(string.Join("\n", result.Diagnostics));
                return stream.ToArray();
            }
            measure("compile-markup-csharp-" + count, 1, () => Compile().Length);
            var load = new AssemblyLoadContext("MarkupProbe-" + count, isCollectible: true);
            load.Resolving += (_, name) => AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(assembly =>
                assembly.GetName().Name == name.Name && AssemblyLoadContext.GetLoadContext(assembly) == AssemblyLoadContext.Default);
            try
            {
                using var input = new MemoryStream(Compile());
                var assembly = load.LoadFromStream(input);
                var factory = assembly.GetType(output.FactoryMetadataName)!.GetMethod(output.BuildMethodName!)!;
                var build = factory.CreateDelegate<Func<IServiceProvider?, object>>();
                var root = build(null);
                var children = (IList)root.GetType().GetProperty("Children")!.GetValue(root)!;
                if (children.Count != count || !XamlRuntimeSession.TryGet(root, out var session))
                    throw new InvalidOperationException("Incomplete markup graph");
                var childType = assembly.GetType("MarkupProbeModel.Item")!;
                var extensionType = assembly.GetType("MarkupProbeModel.EchoExtension")!;
                var valueProperty = childType.GetProperty("Value")!;
                var textProperty = extensionType.GetProperty("Value")!;
                var extraProperty = extensionType.GetProperty("Extra")!;
                for (var i = 0; i < count; i++)
                {
                    var child = children[i]!;
                    var extension = valueProperty.GetValue(child)!;
                    if (!Equals(textProperty.GetValue(extension), "value" + i) || !Equals(extraProperty.GetValue(extension), "extra" + i) ||
                        session!.FindNode(extension)!.ParentKey != session.FindNode(child)!.Key)
                        throw new InvalidOperationException("Incorrect markup value or source ownership");
                }
                // Only semantic metadata enters the cross-revision correctness hash.
                // Generated source/IL size is reported independently, not required equal.
                var signature = JsonSerializer.Serialize(session!.Nodes.OrderBy(node => node.Key, StringComparer.Ordinal)
                    .Select(node => new { node.Key, node.ParentKey, Type = node.Instance.GetType().FullName, node.Source }));
                var semantic = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(signature)));
                session.Dispose();
                measure("construct-markup-" + count, 3, () =>
                {
                    var instance = build(null);
                    if (!XamlRuntimeSession.TryGet(instance, out var current)) throw new InvalidOperationException("Missing construction session");
                    var nodes = current!.Nodes.Count;
                    current.Dispose();
                    return nodes;
                });
                var methods = assembly.GetTypes().SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                    BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly).Cast<MethodBase>()
                    .Concat(type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)))
                    .Select(method => method.GetMethodBody()).Where(body => body != null).ToArray();
                reports.Add(new(count, Encoding.UTF8.GetByteCount(output.Source), methods.Length,
                    methods.Sum(body => body!.GetILAsByteArray()!.Length), methods.Sum(body => body!.LocalVariables.Count),
                    methods.Sum(body => body!.ExceptionHandlingClauses.Count), semantic));
            }
            finally { load.Unload(); }
        }
        return reports;
    }
}
