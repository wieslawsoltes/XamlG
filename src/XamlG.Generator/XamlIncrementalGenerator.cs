using System.Collections.Immutable;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using XamlG.Compiler;
using XamlG.CSharp;
using XamlG.Syntax;

namespace XamlG.Generator;

[Generator(LanguageNames.CSharp)]
public sealed class XamlIncrementalGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var options = context.AnalyzerConfigOptionsProvider
            .Select(static (provider, _) => GeneratorOptions.Read(provider.GlobalOptions))
            .WithTrackingName("XamlG.Options");

        var inputs = context.AdditionalTextsProvider
            .Where(static file => file.Path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase) || file.Path.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase))
            .Combine(context.AnalyzerConfigOptionsProvider)
            .Select(static (pair, cancellation) => GeneratorInput.Read(pair.Left, pair.Right, cancellation))
            .Where(static input => input.Compile)
            .WithTrackingName("XamlG.Input");

        var parsed = inputs.Select(static (input, cancellation) =>
            new ParsedGeneratorInput(input, XamlSyntaxTree.Parse(input.Text, input.LogicalPath, cancellation)))
            .WithTrackingName("XamlG.Parse");

        var environment = context.CompilationProvider.Combine(options)
            .Select(static (pair, _) => new GeneratorEnvironment((CSharpCompilation)pair.Left, pair.Right))
            .WithTrackingName("XamlG.TypeSystem");

        var output = parsed.Combine(environment)
            .Select(static (pair, cancellation) => Compile(pair.Left, pair.Right, cancellation))
            .WithComparer(GeneratorOutputComparer.Instance)
            .WithTrackingName("XamlG.Emit");

        context.RegisterSourceOutput(output, static (production, result) =>
        {
            var text = SourceText.From(result.Text, Encoding.UTF8);
            foreach (var diagnostic in result.Diagnostics)
                production.ReportDiagnostic(GeneratorDiagnosticReporter.Create(result.Path, text, diagnostic));
            if (result.Source.Length != 0)
                production.AddSource(result.HintName, SourceText.From(result.Source, Encoding.UTF8));
        });

        context.RegisterSourceOutput(parsed.Collect().Combine(options), static (production, pair) =>
        {
            if (!pair.Right.Enabled) return;
            foreach (var group in pair.Left.Where(p => p.ClassName != null).GroupBy(p => p.ClassName, StringComparer.Ordinal))
            {
                if (group.Count() < 2) continue;
                foreach (var input in group)
                    production.ReportDiagnostic(GeneratorDiagnosticReporter.Create(input.Input.Path,
                        SourceText.From(input.Input.Text, Encoding.UTF8),
                        new("XG2002", $"More than one XAML document declares x:Class '{group.Key}'.", input.Syntax.Root!.NameSpan)));
            }
        });
    }

    private static GeneratorOutput Compile(ParsedGeneratorInput input, GeneratorEnvironment environment, CancellationToken cancellationToken)
    {
        if (!environment.Options.Enabled)
            return new(input.Input.Path, input.Input.Text, string.Empty, string.Empty, ImmutableArray<XamlDiagnostic>.Empty);
        if (environment.ConfigurationError != null)
            return Failure(input, "XG2000", environment.ConfigurationError);

        try
        {
            var options = new XamlCompilerOptions
            {
                DocumentId = input.Input.LogicalPath,
                GenerateInitializeComponent = environment.Options.GenerateInitializeComponent,
                GenerateNamedFields = environment.Options.GenerateNamedFields
            };
            var bound = new XamlCompiler().Bind(input.Syntax, environment.Types, environment.Profile, options, cancellationToken);
            var emitted = new CSharpEmitter().Emit(bound, cancellationToken);
            return new(input.Input.Path, input.Input.Text, emitted.HintName, emitted.Success ? emitted.Source : string.Empty, emitted.Diagnostics);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            return Failure(input, "XG9000", $"XamlG encountered an internal compiler error: {error.GetType().Name}: {error.Message}");
        }
    }

    private static GeneratorOutput Failure(ParsedGeneratorInput input, string code, string message) =>
        new(input.Input.Path, input.Input.Text, string.Empty, string.Empty,
            ImmutableArray.Create(new XamlDiagnostic(code, message, new XamlG.Syntax.TextSpan(0, 0))));
}
