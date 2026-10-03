using System.Collections.Immutable;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using XamlG.Compiler;
using XamlG.CSharp;
using XamlG.Roslyn;
using XamlG.Runtime;
using XamlG.Syntax;
using XamlX;
using XamlX.Transform;
using XamlX.IL;
using XamlX.TypeSystem;
using Diagnostic = XamlX.XamlDiagnostic;

namespace XamlParserTests;

/// <summary>Upstream test API adapter. All Compile calls bind and emit with XamlG, never with XamlX's compiler.
/// XamlX is linked only to preserve the original tests' configuration and exception contracts.</summary>
public class CompilerTestBase
{
    private static readonly Lazy<ImmutableArray<MetadataReference>> References = new(() =>
        ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
        .Concat(new[] { typeof(CompilerTestBase).Assembly.Location, typeof(XamlRuntimeContext).Assembly.Location, typeof(XamlX.Runtime.IXamlParentStackProviderV1).Assembly.Location })
        .Distinct(StringComparer.Ordinal).Select(p => (MetadataReference)MetadataReference.CreateFromFile(p)).ToImmutableArray());

    public CompilerTestBase()
    {
        var types = new SreTypeSystem();
        Configuration = new TransformerConfiguration(types, types.FindAssembly("XamlParserTests"), new XamlLanguageTypeMappings(types));
    }

    public TransformerConfiguration Configuration { get; }
    public List<Diagnostic> Diagnostics { get; } = new();

    protected (Func<IServiceProvider?, object>? create, Action<IServiceProvider?, object?> populate) Compile(string xaml, bool generateBuildMethod = true)
    {
        var compilation = CreateCompilation();
        var document = Bind(xaml, compilation, generateBuildMethod);
        var failures = document.Diagnostics.Where(d => d.Severity == XamlSeverity.Error).Select(d => Translate(d)).ToArray();
        if (failures.Length == 1) throw failures[0];
        if (failures.Length > 1) throw new AggregateException(failures);
        var output = new CSharpEmitter().Emit(document);
        if (!output.Success) throw new XamlLoadException(string.Join("\n", output.Diagnostics.Select(d => d.Message)), null);
        compilation = compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(output.Source, new CSharpParseOptions(LanguageVersion.Preview), output.HintName));
        using var image = new MemoryStream();
        var emitted = compilation.Emit(image);
        if (!emitted.Success) throw new XamlLoadException(string.Join("\n", emitted.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)) + "\n" + output.Source, null);
        var factory = Assembly.Load(image.ToArray()).GetType(output.FactoryTypeName)!;
        var services = Expression.Parameter(typeof(IServiceProvider));
        var root = Expression.Parameter(typeof(object));
        var populate = factory.GetMethod(output.PopulateMethodName)!;
        var populateCallback = Expression.Lambda<Action<IServiceProvider?, object?>>(Expression.Call(populate, Expression.Convert(root, populate.GetParameters()[0].ParameterType), services), services, root).Compile();
        var create = output.BuildMethodName == null ? null : factory.GetMethod(output.BuildMethodName);
        var createCallback = create == null ? null : Expression.Lambda<Func<IServiceProvider?, object>>(Expression.Convert(Expression.Call(create, services), typeof(object)), services).Compile();
        return (createCallback, populateCallback);
    }

    protected object CompileAndRun(string xaml, IServiceProvider? prov = null) => Compile(xaml).create!(prov);
    protected void CompileAndPopulate(string xaml, IServiceProvider? prov = null, object? instance = null) => Compile(xaml, false).populate(prov, instance);
    public BoundDocument Transform(string xaml) => Bind(xaml, CreateCompilation(), true);

    private static CSharpCompilation CreateCompilation() => CSharpCompilation.Create("XamlG.Upstream." + Guid.NewGuid().ToString("N"), references: References.Value,
        options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));

    protected virtual XamlFrameworkProfile ConfigureProfile(XamlFrameworkProfile profile) => profile;

    private BoundDocument Bind(string xaml, CSharpCompilation compilation, bool generateBuildMethod)
    {
        XamlMethodReference? Method(IXamlMethod? method) => method == null ? null : new(method.DeclaringType.FullName, method.Name);
        var profile = XamlFrameworkProfile.Portable with
        {
            Name = "XamlX upstream fixtures",
            TypeSystem = new XamlTypeSystemConfiguration
            {
                DefaultAssemblyName = "XamlParserTests",
                XmlnsDefinitionAttributes = ImmutableArray.Create("XamlParserTests.XmlnsDefinitionAttribute"),
                ContentAttributes = ImmutableArray.Create("XamlParserTests.ContentAttribute"),
                WhitespaceSignificantCollectionAttributes = ImmutableArray.Create("XamlParserTests.WhitespaceSignificantCollectionAttribute"),
                TrimSurroundingWhitespaceAttributes = ImmutableArray.Create("XamlParserTests.TrimSurroundingWhitespaceAttribute"),
                UsableDuringInitializationAttributes = ImmutableArray.Create("XamlParserTests.UsableDuringInitializationAttribute"),
                DeferredContentAttributes = ImmutableArray.Create("XamlParserTests.DeferredContentAttribute"),
                AddChildInterfaces = ImmutableArray.Create("XamlParserTests.IAddChild", "XamlParserTests.IAddChild`1")
            },
            Runtime = new XamlRuntimeConfiguration
            {
                TargetPropertyMode = XamlTargetPropertyMode.Name,
                ProtectNamespaceDictionaries = false,
                UseTypeDescriptorStubs = true,
                InnerServiceProviderFactory = Method(Configuration.TypeMappings.InnerServiceProviderFactoryMethod),
                DeferredContentCustomizer = Method(Configuration.TypeMappings.DeferredContentExecutorCustomization),
                Services = ImmutableArray.Create(
                    new XamlServiceMapping("XamlParserTests.ITestRootObjectProvider", XamlServiceKind.RootObject),
                    new XamlServiceMapping("XamlParserTests.ITestProvideValueTarget", XamlServiceKind.ProvideValueTarget),
                    new XamlServiceMapping("XamlParserTests.ITestUriContext", XamlServiceKind.UriContext),
                    new XamlServiceMapping("XamlX.Runtime.IXamlParentStackProviderV1", XamlServiceKind.ParentStack),
                    new XamlServiceMapping("XamlX.Runtime.IXamlXmlNamespaceInfoProviderV1", XamlServiceKind.XmlNamespaces, "XamlX.Runtime.XamlXmlNamespaceInfoV1"))
            }
        };
        var document = new XamlCompiler().Bind(XamlSyntaxTree.Parse(xaml, "Upstream.xaml"), compilation, ConfigureProfile(profile),
            new XamlCompilerOptions { GenerateBuildMethod = generateBuildMethod, BaseUri = "http://example.com/" });
        foreach (var diagnostic in document.Diagnostics)
        {
            var position = document.Syntax.Lines.GetPosition(Math.Min(diagnostic.Span.Start, xaml.Length));
            Diagnostics.Add(new(diagnostic.Code == "XG2001" ? "Obsolete" : diagnostic.Code,
                diagnostic.Severity == XamlSeverity.Error ? XamlDiagnosticSeverity.Error : XamlDiagnosticSeverity.Warning,
                diagnostic.Message, position.Line + 1, position.Character + 1));
        }
        return document;
    }

    private static Exception Translate(XamlG.Syntax.XamlDiagnostic diagnostic)
    {
        if (diagnostic.Code == "XG1006") return new InvalidOperationException(diagnostic.Message);
        if (diagnostic.Code is "XG1025" or "XG1026" or "XG1004" or "XG1003" or "XG0011") return new XamlTransformException(diagnostic.Message, null);
        return new XamlLoadException(diagnostic.Code == "XG1020" ? "XamlX.Ast.XamlAstXmlDirective: " + diagnostic.Message : diagnostic.Message, null);
    }
}
