using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using XamlG.CSharp.Integration;
using XamlG.Frameworks;
using XamlG.Syntax;
using XamlG.Tooling;
using XamlG.Workspaces;

namespace XamlG.Cli;

internal sealed class CompilerCommand
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    public async Task<int> RunAsync(CliOptions options, CancellationToken cancellationToken)
    {
        if (options.Command == "help")
        {
            Console.WriteLine("""
                XamlG — Roslyn-native XAML compiler
                xamlg compile --project App.csproj --output obj/XamlG
                xamlg check --project App.csproj --json
                xamlg inspect --file View.xaml --code Model.cs --framework Portable
                xamlg compile --file View.xaml --reference Controls.dll --emit-assembly View.dll
                Commands: compile, check, inspect
                Options: --project, --file, --code (repeatable), --reference (repeatable),
                         --framework Auto|Portable|Avalonia, --target-framework,
                         --output, --emit-assembly, --json
                --project evaluates trusted MSBuild and runs its source generators.
                --emit-assembly in project mode also prepares evaluated embedded resources.
                Standalone compilation reads metadata only. Generated code is never executed.
                Project compilation retains all resource factories and loader adapters.
                """);
            return 0;
        }
        XamlWorkspaceHost? host = null;
        try
        {
            XamlCompilationSession session;
            XamlWorkspaceOptions? workspaceOptions = null;
            ImmutableArray<XamlSyntaxTree> documents;
            var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
            if (options.Project != null)
            {
                var properties = ImmutableDictionary<string, string>.Empty;
                if (options.TargetFramework != null) properties = properties.Add("TargetFramework", options.TargetFramework);
                workspaceOptions = new() { AllowProjectEvaluation = true, Framework = options.Framework, GlobalProperties = properties };
                host = XamlWorkspaceHost.Create(workspaceOptions);
                var project = await host.OpenProjectAsync(options.Project, cancellationToken);
                foreach (var diagnostic in host.Diagnostics) Console.Error.WriteLine("workspace: " + diagnostic.Message);
                session = project.Compiler;
                parseOptions = project.Project.ParseOptions as CSharpParseOptions
                    ?? throw new InvalidOperationException("The evaluated project has no C# parse options.");
                documents = project.Documents.Values.OrderBy(d => d.Path, StringComparer.Ordinal).ToImmutableArray();
                if (options.File != null && !project.Documents.Keys.Any(path => Path.GetFullPath(path) == Path.GetFullPath(options.File)))
                    throw new ArgumentException("The requested --file does not belong to the evaluated project.");
            }
            else
            {
                var references = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                    .Concat(options.References.Select(Path.GetFullPath)).Distinct(StringComparer.Ordinal).Select(p => MetadataReference.CreateFromFile(p)).ToArray();
                var trees = new List<SyntaxTree>();
                foreach (var file in options.CodeFiles)
                    trees.Add(CSharpSyntaxTree.ParseText(await File.ReadAllTextAsync(file, cancellationToken), parseOptions, file, cancellationToken: cancellationToken));
                var compilation = CSharpCompilation.Create("XamlG.Standalone", trees, references,
                    new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
                session = new(compilation, KnownFrameworkProfiles.Select(compilation, options.Framework));
                documents = ImmutableArray.Create(XamlSyntaxTree.Parse(await File.ReadAllTextAsync(options.File!, cancellationToken), options.File!, cancellationToken));
            }
            if (documents.IsEmpty) throw new InvalidOperationException("No XAML AdditionalFiles were found. Add XamlG.Generator to the project or use --file.");
            var compiledProject = session.CompileProject(documents, cancellationToken);
            var analyses = compiledProject.Documents.Select(d => new XamlAnalysis(d.Input.Syntax, d.Document, d.Output)).ToImmutableArray();
            var diagnostics = analyses.SelectMany(a => a.Output.Diagnostics.Select(d =>
            {
                var position = a.Syntax.Lines.GetPosition(Math.Min(d.Span.Start, a.Syntax.Text.Length));
                return new CliDiagnostic(d.Code, d.Message, d.Severity.ToString(), a.Syntax.Path, position.Line + 1, position.Character + 1);
            })).ToList();
            var compilationWithOutput = XamlCSharpCompilation.AddGeneratedSources(session.Types.Compilation, compiledProject, parseOptions, cancellationToken);
            foreach (var diagnostic in compiledProject.SourceIntegration.Diagnostics.Concat(compilationWithOutput.GetDiagnostics(cancellationToken))
                .Where(d => d.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning))
            {
                var position = diagnostic.Location.GetMappedLineSpan();
                diagnostics.Add(new(diagnostic.Id, diagnostic.GetMessage(), diagnostic.Severity.ToString(), position.Path, position.StartLinePosition.Line + 1, position.StartLinePosition.Character + 1));
            }
            // Preserve the existing inspect array shape; project adapter source is an additive field.
            if (options.Command == "inspect")
                Console.WriteLine(JsonSerializer.Serialize(analyses.Select(a => new
                {
                    path = a.Syntax.Path, syntax = XamlInspector.Syntax(a.Syntax), bound = XamlInspector.Bound(a.Document),
                    sourceMappings = a.Output.SourceMappings, generated = a.Output.Source,
                    integration = compiledProject.SourceIntegration.Source, diagnostics
                }), JsonOptions));
            else if (options.Json) Console.WriteLine(JsonSerializer.Serialize(diagnostics, JsonOptions));
            else foreach (var item in diagnostics) Console.Error.WriteLine($"{item.Path}({item.Line},{item.Column}): {item.Severity.ToLowerInvariant()} {item.Code}: {item.Message}");
            if (diagnostics.Any(d => d.Severity == "Error") || !compiledProject.Success) return 1;
            // Complete emission in memory before modifying compiler-owned output files.
            byte[]? emittedImage = null;
            if (options.AssemblyOutput != null)
            {
                using var image = new MemoryStream();
                var inputs = options.Project == null ? null : await XamlMSBuildEmissionCollector.CollectAsync(options.Project, workspaceOptions!, cancellationToken);
                var result = inputs == null ? compilationWithOutput.Emit(image, cancellationToken: cancellationToken)
                    : inputs.Emit(compilationWithOutput, image, cancellationToken);
                if (!result.Success) throw new InvalidOperationException(string.Join("\n", result.Diagnostics));
                emittedImage = image.ToArray();
            }
            if (options.Command == "compile")
            {
                await GeneratedOutputWriter.WriteAsync(options.Output, XamlCSharpCompilation.Sources(compiledProject), cancellationToken);
                if (!options.Json) Console.WriteLine($"Generated {analyses.Length} document(s) and loader adapters into {Path.GetFullPath(options.Output)}.");
            }
            if (emittedImage != null)
            {
                var output = Path.GetFullPath(options.AssemblyOutput!);
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                var temporary = output + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try { await File.WriteAllBytesAsync(temporary, emittedImage, cancellationToken); File.Move(temporary, output, overwrite: true); }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
            return 0;
        }
        finally { host?.Dispose(); }
    }
}
