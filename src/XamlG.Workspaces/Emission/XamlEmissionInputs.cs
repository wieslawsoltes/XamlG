using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;

namespace XamlG.Workspaces;

/// <summary>Evaluated compiler resources, parsed by Roslyn rather than inferred from filenames.
/// Resource providers open the original build-generated payload when emission occurs.</summary>
public sealed class XamlEmissionInputs
{
    private readonly CSharpCommandLineArguments _arguments;
    private XamlEmissionInputs(CSharpCommandLineArguments arguments) => _arguments = arguments;
    public ImmutableArray<ResourceDescription> ManagedResources => _arguments.ManifestResources;

    public static XamlEmissionInputs FromCommandLine(IEnumerable<string> arguments, string projectDirectory)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectDirectory);
        var parsed = CSharpCommandLineParser.Default.Parse(arguments, Path.GetFullPath(projectDirectory), sdkDirectory: null);
        var errors = parsed.Errors.Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
        if (errors.Length != 0) throw new InvalidDataException("Invalid evaluated compiler inputs: " + string.Join("\n", errors.Select(d => d.ToString())));
        return new(parsed);
    }

    public EmitResult Emit(CSharpCompilation compilation, Stream image, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(compilation);
        ArgumentNullException.ThrowIfNull(image);
        cancellationToken.ThrowIfCancellationRequested();
        using var manifest = Open(_arguments.Win32Manifest);
        using var icon = Open(_arguments.Win32Icon);
        using var native = _arguments.Win32ResourceFile == null
            ? compilation.CreateDefaultWin32Resources(versionResource: true, noManifest: _arguments.NoWin32Manifest,
                manifestContents: manifest, iconInIcoFormat: icon)
            : File.OpenRead(_arguments.Win32ResourceFile);
        return compilation.Emit(image, manifestResources: ManagedResources, win32Resources: native, cancellationToken: cancellationToken);
    }
    private static Stream? Open(string? path) => path == null ? null : File.OpenRead(path);
}
