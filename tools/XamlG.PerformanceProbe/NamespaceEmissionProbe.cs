using System.Collections.Immutable;
using System.Linq.Expressions;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using XamlG.Compiler;
using XamlG.CSharp;
using XamlG.Roslyn;
using XamlG.Runtime;
using XamlG.Syntax;

/// <summary>Fresh namespace plans, small and large, with strict source equality.
/// Binding, reflection-bridge compilation and generated-code validation are not timed.</summary>
internal static class NamespaceEmissionProbe
{
    public static string Run(Action<string, int, Func<int>> measure)
    {
        const string model = """
            using System;
            using System.Collections.Generic;
            using XamlG.Runtime;
            namespace NamespaceProbeModel {
                public interface INamespaces { IReadOnlyDictionary<string,IReadOnlyList<NamespaceItem>> XmlNamespaces {get;} }
                public class NamespaceItem { public string ClrNamespace {get;set;} public string ClrAssemblyName {get;set;} }
                public class Owner { [Content] public List<Owner> Children {get;} = new(); public object Value {get;set;} }
                public class SnapshotExtension {
                    public object ProvideValue(IServiceProvider services) => ((INamespaces)services.GetService(typeof(INamespaces))).XmlNamespaces;
                }
            }
            """;
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Append(typeof(XamlRuntimeContext).Assembly.Location).Distinct(StringComparer.Ordinal)
            .Select(path => MetadataReference.CreateFromFile(path)).ToArray();
        var parse = new CSharpParseOptions(LanguageVersion.Preview);
        var compilation = CSharpCompilation.Create("NamespaceEmissionProbe", [CSharpSyntaxTree.ParseText(model, parse)], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release));
        var profile = XamlFrameworkProfile.Portable with
        {
            Runtime = new() { Services = ImmutableArray.Create(new XamlServiceMapping("NamespaceProbeModel.INamespaces", XamlServiceKind.XmlNamespaces)) }
        };
        var (createContext, createEmitter, createWriter, emitFactory) = Bridges();
        var signature = new StringBuilder();
        foreach (var (count, aliases, mappings) in new[] { (1, 2, 8), (32, 16, 512), (128, 16, 512) })
        {
            var xaml = "<Owner xmlns='clr-namespace:NamespaceProbeModel' " + string.Join(" ", Enumerable.Range(0, aliases)
                .Select(i => "xmlns:p" + i + "='urn:binding" + i + "'")) + ">" +
                string.Concat(Enumerable.Range(0, count).Select(i => "<Owner xmlns:shadow='using:Shadow" + i + "' Value='{Snapshot}'/>")) + "</Owner>";
            var syntax = XamlSyntaxTree.Parse(xaml, "Namespaces.xaml");
            var document = new XamlCompiler().Bind(syntax, compilation, profile);
            Require(document.Success, "Namespace fixture failed to bind: " + string.Join("\n", document.Diagnostics));
            document = document with
            {
                Runtime = document.Runtime with
                {
                    NamespaceMappings = document.Runtime.NamespaceMappings.AddRange(Enumerable.Range(0, mappings)
                        .Select(i => new XmlNamespaceMapping("urn:binding" + i % 128, "Mapped" + i, i % 3 == 0 ? null : "Assembly" + i)))
                }
            };
            var rootScope = NamespaceScope.Empty.Push(syntax.Root!);
            var scopes = syntax.Root!.Children.OfType<XamlElementSyntax>().Select(rootScope.Push).ToArray();
            var names = Enumerable.Range(0, scopes.Length).Select(i => "Create" + i).ToArray();
            string Factories()
            {
                using var context = createContext(document);
                var emitter = createEmitter(context);
                var writer = createWriter();
                for (var i = 0; i < scopes.Length; i++) emitFactory(emitter, writer, scopes[i], names[i]);
                return writer.ToString()!;
            }
            var factorySource = Factories();
            var output = new CSharpEmitter().Emit(document);
            Require(output.Success, "Namespace fixture failed to emit");
            var generated = compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(output.Source, parse),
                CSharpSyntaxTree.ParseText("public static class NamespaceFactories {\n" + factorySource + "}\n", parse));
            using (var assembly = new MemoryStream())
            {
                var result = generated.Emit(assembly);
                Require(result.Success, "Namespace-generated C# failed: " + string.Join("\n", result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
            }
            var suffix = count + "x" + aliases + "-mappings" + mappings;
            var operations = count == 1 ? 8 : 1;
            measure("namespace-factories-" + suffix, operations, () => Factories().Length);
            measure("emit-namespaces-" + suffix, operations, () => new CSharpEmitter().Emit(document).Source.Length);
            signature.Append(suffix).Append('\n').Append(factorySource).Append('\n').Append(output.Source)
                .Append(JsonSerializer.Serialize(output.SourceMappings)).Append(JsonSerializer.Serialize(output.Diagnostics));
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(signature.ToString())));
    }

    private static (Func<BoundDocument, IDisposable> Context, Func<IDisposable, object> Emitter,
        Func<object> Writer, Action<object, object, NamespaceScope, string> Emit) Bridges()
    {
        // Both compiler revisions expose these same internal entry points. Bind
        // delegates once, not reflection calls inside the measured alias loops.
        var assembly = typeof(CSharpEmitter).Assembly;
        var contextType = assembly.GetType("XamlG.CSharp.EmissionContext", true)!;
        var emitterType = assembly.GetType("XamlG.CSharp.NamespaceMapEmitter", true)!;
        var writerType = assembly.GetType("XamlG.CSharp.CSharpWriter", true)!;
        var document = Expression.Parameter(typeof(BoundDocument));
        var createContext = Expression.Lambda<Func<BoundDocument, IDisposable>>(Expression.Convert(
            Expression.New(contextType.GetConstructor([typeof(BoundDocument), typeof(CancellationToken)])!, document,
                Expression.Constant(CancellationToken.None)), typeof(IDisposable)), document).Compile();
        var context = Expression.Parameter(typeof(IDisposable));
        var createEmitter = Expression.Lambda<Func<IDisposable, object>>(Expression.Convert(
            Expression.New(emitterType.GetConstructor([contextType])!, Expression.Convert(context, contextType)), typeof(object)), context).Compile();
        var createWriter = Expression.Lambda<Func<object>>(Expression.Convert(Expression.New(writerType), typeof(object))).Compile();
        var emitter = Expression.Parameter(typeof(object));
        var writer = Expression.Parameter(typeof(object));
        var scope = Expression.Parameter(typeof(NamespaceScope));
        var name = Expression.Parameter(typeof(string));
        var emit = Expression.Lambda<Action<object, object, NamespaceScope, string>>(Expression.Call(Expression.Convert(emitter, emitterType),
            emitterType.GetMethod("EmitFactory", BindingFlags.NonPublic | BindingFlags.Instance)!, Expression.Convert(writer, writerType), scope,
            name, Expression.Constant("public")), emitter, writer, scope, name).Compile();
        return (createContext, createEmitter, createWriter, emit);
    }

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
