using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Styling;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using XamlG.CSharp.Integration;
using XamlG.CSharp.Resources;
using XamlG.Frameworks.Avalonia;
using XamlG.Runtime;
using XamlG.Syntax;

namespace XamlG.ThemeCorpus;

/// <summary>Compiles original theme XAML and code-behind together, then constructs the
/// generated root and realizes controls. No original theme assembly is used as a fallback.</summary>
internal static class ThemeCorpusRunner
{
    public static void Run(string checkout, string theme, int expectedCount, string evidence, CancellationToken cancellationToken)
    {
        if (theme is not ("Simple" or "Fluent")) throw new ArgumentException("Unknown theme corpus.", nameof(theme));
        var assemblyName = "Avalonia.Themes." + theme;
        var directory = Path.Combine(checkout, "src", assemblyName);
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException(directory);
        Directory.CreateDirectory(evidence);
        var files = Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
            .Where(file => !Path.GetRelativePath(directory, file).Split(Path.DirectorySeparatorChar).Any(p => p is "bin" or "obj"))
            .OrderBy(file => Path.GetRelativePath(directory, file), StringComparer.Ordinal).ToArray();
        var xaml = ThemeSourceCatalog.Read(checkout, directory, expectedCount);
        File.WriteAllText(Path.Combine(evidence, "input-manifest.json"), JsonSerializer.Serialize(
            xaml.Concat(files.Where(file => file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                .Select(file => new ThemeSourceInput(file, Path.GetRelativePath(directory, file).Replace('\\', '/'), false))).Select(input => new
            {
                path = Path.GetRelativePath(checkout, input.PhysicalPath).Replace('\\', '/'),
                logicalPath = input.LogicalPath,
                linked = input.IsLinked,
                sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(input.PhysicalPath)))
            }), new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"{assemblyName}: {expectedCount} physical and {xaml.Count(input => input.IsLinked)} declared linked XAML documents.");

        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview, preprocessorSymbols: new[]
        { "NET", "NET10_0", "NET10_0_OR_GREATER", "NET9_0_OR_GREATER", "NET8_0_OR_GREATER", "NET7_0_OR_GREATER", "NET6_0_OR_GREATER" });
        var trees = files.Where(file => file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)).Select(file =>
            CSharpSyntaxTree.ParseText(File.ReadAllText(file), parseOptions, file, cancellationToken: cancellationToken)).ToArray();
        var referencePaths = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Concat(Directory.GetFiles(AppContext.BaseDirectory, "*.dll"))
            .Where(file => !Path.GetFileName(file).StartsWith("Avalonia.Themes.", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal).ToArray();
        var references = referencePaths.Select(file => MetadataReference.CreateFromFile(file));
        var compilation = CSharpCompilation.Create(assemblyName, trees, references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true,
                optimizationLevel: OptimizationLevel.Release, nullableContextOptions: NullableContextOptions.Enable));
        var inputs = xaml.Select(input => new XamlProjectDocument(
            XamlSyntaxTree.Parse(File.ReadAllText(input.PhysicalPath), input.PhysicalPath, cancellationToken), input.LogicalPath)).ToArray();
        // Match the upstream theme projects' reflection-binding default. A compiled-binding
        // error is never retried through reflection or the original XamlX backend.
        var result = new XamlProjectCompiler().Compile(inputs, compilation,
            AvaloniaFrameworkProfile.Create(compileBindingsByDefault: false), cancellationToken: cancellationToken);
        var xamlDiagnostics = result.Documents.SelectMany(document => document.Output.Diagnostics.Select(diagnostic => new
        { path = document.Input.LogicalPath, code = diagnostic.Code, message = diagnostic.Message, span = diagnostic.Span.ToString(), severity = diagnostic.Severity.ToString() })).ToArray();
        File.WriteAllText(Path.Combine(evidence, "xaml-diagnostics.json"), JsonSerializer.Serialize(xamlDiagnostics, new JsonSerializerOptions { WriteIndented = true }));
        foreach (var diagnostic in xamlDiagnostics.Where(d => d.severity == "Error").Take(80))
            Console.Error.WriteLine($"{diagnostic.path}: {diagnostic.code}: {diagnostic.message}");
        foreach (var diagnostic in result.SourceIntegration.Diagnostics) Console.Error.WriteLine(diagnostic);
        if (!result.Success) throw new InvalidOperationException($"{assemblyName} failed XamlG binding or source integration. Full diagnostics are retained.");

        compilation = XamlCSharpCompilation.AddGeneratedSources(compilation, result, parseOptions, cancellationToken);
        var generatedDirectory = Path.Combine(evidence, "generated");
        Directory.CreateDirectory(generatedDirectory);
        foreach (var source in XamlCSharpCompilation.Sources(result))
            File.WriteAllText(Path.Combine(generatedDirectory, source.HintName), source.Source);
        using var image = new MemoryStream();
        var emitted = compilation.Emit(image, cancellationToken: cancellationToken);
        File.WriteAllLines(Path.Combine(evidence, "emit-diagnostics.txt"), emitted.Diagnostics.Select(d => d.ToString()));
        if (!emitted.Success)
            throw new InvalidOperationException(string.Join("\n", emitted.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Take(80)));
        image.Position = 0;
        AppBuilder.Configure<Application>().UseHeadless(new AvaloniaHeadlessPlatformOptions()).SetupWithoutStarting();
        var assembly = AssemblyLoadContext.Default.LoadFromStream(image);
        var output = result.Documents.Single(d => d.Input.LogicalPath == theme + "Theme.xaml").Output;
        if (output.BuildMethodName == null) throw new InvalidOperationException("The original theme class has no generated construction factory.");
        var factory = assembly.GetType(output.FactoryMetadataName, throwOnError: true)!;
        var build = factory.GetMethod(output.BuildMethodName, BindingFlags.Public | BindingFlags.Static)
            ?? throw new MissingMethodException(factory.FullName, output.BuildMethodName);
        var root = build.Invoke(null, new object?[] { null }) as IStyle
            ?? throw new InvalidOperationException("The compiled theme factory did not return IStyle.");
        Application.Current!.Styles.Add(root);
        try
        {
            var realized = ThemeControlAcceptance.Realize(cancellationToken);
            File.WriteAllText(Path.Combine(evidence, "result.json"), JsonSerializer.Serialize(new
            {
                theme, documents = inputs.Length, physicalDocuments = expectedCount, linkedDocuments = xaml.Count(input => input.IsLinked),
                codeFiles = trees.Length, realized,
                assemblySha256 = Convert.ToHexString(SHA256.HashData(image.ToArray())),
                sourceFactories = result.Documents.Length, success = true
            }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"PASS: {inputs.Length} unmodified {theme} documents including project links, original code-behind, root construction, {realized} realized control/theme cases.");
        }
        finally
        {
            Application.Current.Styles.Remove(root);
            if (XamlRuntimeSession.TryGet(root, out var session)) session!.Dispose();
        }
    }
}
