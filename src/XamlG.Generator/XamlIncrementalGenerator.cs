using System.Collections.Immutable;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using XamlG.Compiler;
using XamlG.CSharp.Resources;
using XamlG.Syntax;

namespace XamlG.Generator;

[Generator(LanguageNames.CSharp)]
public sealed class XamlIncrementalGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var options = context.AnalyzerConfigOptionsProvider
            .Select(static (provider, _) => GeneratorOptions.Read(provider.GlobalOptions)).WithTrackingName("XamlG.Options");
        var inputs = context.AdditionalTextsProvider
            .Where(static file => file.Path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase) || file.Path.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase))
            .Combine(context.AnalyzerConfigOptionsProvider)
            .Select(static (pair, cancellation) => GeneratorInput.Read(pair.Left, pair.Right, cancellation))
            .Where(static input => input.Compile).WithTrackingName("XamlG.Input");
        var parsed = inputs.Select(static (input, cancellation) =>
            new ParsedGeneratorInput(input, XamlSyntaxTree.Parse(input.Text, input.LogicalPath, cancellation))).WithTrackingName("XamlG.Parse");
        var environment = context.CompilationProvider.Combine(options)
            .Select(static (pair, _) => new GeneratorEnvironment((CSharpCompilation)pair.Left, pair.Right)).WithTrackingName("XamlG.TypeSystem");
        var output = parsed.Collect().Combine(environment)
            .SelectMany(static (pair, cancellation) => Compile(pair.Left, pair.Right, cancellation))
            .WithComparer(GeneratorOutputComparer.Instance).WithTrackingName("XamlG.Emit");
        context.RegisterSourceOutput(output, static (production, result) =>
        {
            var text = SourceText.From(result.Text, Encoding.UTF8);
            foreach (var diagnostic in result.Diagnostics)
                production.ReportDiagnostic(GeneratorDiagnosticReporter.Create(result.Path, text, diagnostic));
            if (result.Source.Length != 0) production.AddSource(result.HintName, SourceText.From(result.Source, Encoding.UTF8));
        });
    }
    private static ImmutableArray<GeneratorOutput> Compile(ImmutableArray<ParsedGeneratorInput> inputs,
        GeneratorEnvironment environment, CancellationToken cancellationToken)
    {
        if (!environment.Options.Enabled) return ImmutableArray<GeneratorOutput>.Empty;
        if (environment.ConfigurationError != null)
            return inputs.Select(input => Failure(input, "XG2000", environment.ConfigurationError)).ToImmutableArray();
        try
        {
            var options = new XamlCompilerOptions
            {
                GenerateInitializeComponent = environment.Options.GenerateInitializeComponent,
                GenerateNamedFields = environment.Options.GenerateNamedFields
            };
            var documents = inputs.Select(input => new XamlProjectDocument(input.Syntax, input.Input.LogicalPath));
            var project = new XamlProjectCompiler().Compile(documents, environment.Compilation, environment.Profile, options, cancellationToken);
            var bySyntax = inputs.ToDictionary(i => i.Syntax);
            return project.Documents.Select(result =>
            {
                var input = bySyntax[result.Input.Syntax].Input;
                return new GeneratorOutput(input.Path, input.Text, result.Output.HintName,
                    result.Output.Success ? result.Output.Source : string.Empty, result.Output.Diagnostics);
            }).ToImmutableArray();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            return inputs.Select(input => Failure(input, "XG9000", "XamlG encountered an internal compiler error: " + error.GetType().Name + ": " + error.Message)).ToImmutableArray();
        }
    }
    private static GeneratorOutput Failure(ParsedGeneratorInput input, string code, string message) =>
        new(input.Input.Path, input.Input.Text, string.Empty, string.Empty,
            ImmutableArray.Create(new XamlDiagnostic(code, message, new XamlG.Syntax.TextSpan(0, 0))));
}
