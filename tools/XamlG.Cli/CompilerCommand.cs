using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
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

                --project evaluates a trusted MSBuild project and runs its source generators.
                Standalone compilation reads metadata only; it does not execute the generated code.
                """);
            return 0;
        }
        XamlWorkspaceHost? host = null;
        try
        {
            XamlCompilationSession session;
            ImmutableArray<XamlSyntaxTree> documents;
            var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
            if (options.Project != null)
            {
                var properties = ImmutableDictionary<string, string>.Empty;
                if (options.TargetFramework != null) properties = properties.Add("TargetFramework", options.TargetFramework);
                host = XamlWorkspaceHost.Create(new() { AllowProjectEvaluation = true, Framework = options.Framework, GlobalProperties = properties });
                var project = await host.OpenProjectAsync(options.Project, cancellationToken);
                foreach (var diagnostic in host.Diagnostics) Console.Error.WriteLine("workspace: " + diagnostic.Message);
                session = project.Compiler;
                // Preserve the evaluated project's language version, feature switches and symbols.
                // A fresh options object can make Roslyn reject the entire compilation, even when
                // the apparent language version is identical. Project.ParseOptions also works
                // for a project which initially contains no C# syntax trees.
                parseOptions = project.Project.ParseOptions as CSharpParseOptions
                    ?? throw new InvalidOperationException("The evaluated C# project has no C# parse options.");
                documents = project.Documents.Values.OrderBy(d => d.Path, StringComparer.Ordinal).ToImmutableArray();
                if (options.File != null)
                {
                    var fullPath = Path.GetFullPath(options.File);
                    documents = project.Documents.Where(p => Path.GetFullPath(p.Key) == fullPath).Select(p => p.Value).ToImmutableArray();
                }
            }
            else
            {
                var references = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                    .Concat(options.References.Select(Path.GetFullPath)).Distinct(StringComparer.Ordinal).Select(p => MetadataReference.CreateFromFile(p)).ToArray();
                var trees = new List<SyntaxTree>();
                foreach (var file in options.CodeFiles)
                    trees.Add(CSharpSyntaxTree.ParseText(await System.IO.File.ReadAllTextAsync(file, cancellationToken), parseOptions, file, cancellationToken: cancellationToken));
                var compilation = CSharpCompilation.Create("XamlG.Standalone", trees, references,
                    new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
                session = new(compilation, KnownFrameworkProfiles.Select(compilation, options.Framework));
                documents = ImmutableArray.Create(XamlSyntaxTree.Parse(await System.IO.File.ReadAllTextAsync(options.File!, cancellationToken), options.File!, cancellationToken));
            }
            if (documents.IsEmpty) throw new InvalidOperationException("No XAML AdditionalFiles were found. Add XamlG.Generator to the project or specify --file for a standalone compilation.");
            var analyses = documents.Select(d => session.Analyze(d, cancellationToken)).ToArray();
            var diagnostics = analyses.SelectMany(a => a.Output.Diagnostics.Select(d =>
            {
                var position = a.Syntax.Lines.GetPosition(Math.Min(d.Span.Start, a.Syntax.Text.Length));
                return new CliDiagnostic(d.Code, d.Message, d.Severity.ToString(), a.Syntax.Path, position.Line + 1, position.Character + 1);
            })).ToList();
            var compilationWithOutput = session.Types.Compilation.AddSyntaxTrees(analyses.Where(a => a.Output.Success)
                .Select(a => CSharpSyntaxTree.ParseText(a.Output.Source, parseOptions, a.Output.HintName, cancellationToken: cancellationToken)));
            foreach (var diagnostic in compilationWithOutput.GetDiagnostics(cancellationToken).Where(d => d.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning))
            {
                var position = diagnostic.Location.GetMappedLineSpan();
                diagnostics.Add(new(diagnostic.Id, diagnostic.GetMessage(), diagnostic.Severity.ToString(), position.Path, position.StartLinePosition.Line + 1, position.StartLinePosition.Character + 1));
            }
            if (options.Command == "inspect")
            {
                Console.WriteLine(JsonSerializer.Serialize(analyses.Select(a => new { path = a.Syntax.Path, syntax = XamlInspector.Syntax(a.Syntax), bound = XamlInspector.Bound(a.Document), sourceMappings = a.Output.SourceMappings, generated = a.Output.Source, diagnostics }), JsonOptions));
            }
            else if (options.Json) Console.WriteLine(JsonSerializer.Serialize(diagnostics, JsonOptions));
            else foreach (var item in diagnostics) Console.Error.WriteLine($"{item.Path}({item.Line},{item.Column}): {item.Severity.ToLowerInvariant()} {item.Code}: {item.Message}");
            if (diagnostics.Any(d => d.Severity == "Error") || analyses.Any(a => !a.Output.Success)) return 1;
            if (options.Command == "compile")
            {
                await GeneratedOutputWriter.WriteAsync(options.Output, analyses, cancellationToken);
                if (!options.Json) Console.WriteLine($"Generated {analyses.Length} document(s) into {Path.GetFullPath(options.Output)}.");
            }
            if (options.AssemblyOutput != null)
            {
                using var image = new MemoryStream();
                var result = compilationWithOutput.Emit(image, cancellationToken: cancellationToken);
                if (!result.Success) throw new InvalidOperationException(string.Join("\n", result.Diagnostics));
                var output = Path.GetFullPath(options.AssemblyOutput);
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                await System.IO.File.WriteAllBytesAsync(output, image.ToArray(), cancellationToken);
            }
            return 0;
        }
        finally { host?.Dispose(); }
    }
}
