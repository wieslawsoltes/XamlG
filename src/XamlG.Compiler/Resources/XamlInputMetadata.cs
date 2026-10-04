using Microsoft.CodeAnalysis.Diagnostics;

namespace XamlG.Compiler.Resources;

/// <summary>The generator and workspace interpret AdditionalFiles with the same lexical path policy.
/// No files are opened and paths are not resolved against the compiler process's working directory.</summary>
public static class XamlInputMetadata
{
    public const string LogicalPathKey = "build_metadata.AdditionalFiles.XamlGLogicalPath";
    public const string CompileKey = "build_metadata.AdditionalFiles.XamlGCompile";
    public const string ProjectDirectoryKey = "build_property.MSBuildProjectDirectory";

    public static bool ShouldCompile(AnalyzerConfigOptions metadata)
    {
        if (metadata == null) throw new ArgumentNullException(nameof(metadata));
        return !metadata.TryGetValue(CompileKey, out var text) || !bool.TryParse(text, out var enabled) || enabled;
    }

    public static string LogicalPath(string physicalPath, AnalyzerConfigOptions metadata,
        AnalyzerConfigOptions globals, string? fallbackProjectDirectory = null)
    {
        if (physicalPath == null) throw new ArgumentNullException(nameof(physicalPath));
        if (metadata == null) throw new ArgumentNullException(nameof(metadata));
        if (globals == null) throw new ArgumentNullException(nameof(globals));
        var logical = Normalize(metadata.TryGetValue(LogicalPathKey, out var configured) &&
            !string.IsNullOrWhiteSpace(configured) ? configured : physicalPath);
        var directory = globals.TryGetValue(ProjectDirectoryKey, out var evaluated) &&
            !string.IsNullOrWhiteSpace(evaluated) ? evaluated : fallbackProjectDirectory;
        if (!string.IsNullOrWhiteSpace(directory))
        {
            var prefix = Normalize(directory!).TrimEnd('/') + "/";
            var comparison = prefix.StartsWith("//", StringComparison.Ordinal) || prefix.Length > 1 && prefix[1] == ':'
                ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (logical.StartsWith(prefix, comparison)) logical = logical.Substring(prefix.Length);
        }
        while (logical.StartsWith("./", StringComparison.Ordinal)) logical = logical.Substring(2);
        // Do not invent a basename identity for an external document. Linked-file metadata
        // supplies its project-visible address; the catalog performs resource URI validation.
        return logical;
    }
    private static string Normalize(string path) => path.Replace('\\', '/');
}
