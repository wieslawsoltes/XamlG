using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace XamlG.Tooling;

/// <summary>Serializable compiler settings for in-memory hosts. Normalization validates
/// named Roslyn options and copies collections before they become project state.</summary>
public sealed record CSharpCompilationSettings
{
    public string LanguageVersion { get; init; } = "Preview";
    public string Nullable { get; init; } = "Enable";
    public bool AllowUnsafe { get; init; } = true;
    public bool CheckOverflow { get; init; }
    public string Optimization { get; init; } = "Release";
    public string OutputKind { get; init; } = "DynamicallyLinkedLibrary";
    public string Platform { get; init; } = "AnyCpu";
    public bool Deterministic { get; init; }
    public bool ConcurrentBuild { get; init; } = true;
    public bool ReportSuppressedDiagnostics { get; init; }
    public string MetadataImport { get; init; } = "Public";
    public string DocumentationMode { get; init; } = "Parse";
    public int WarningLevel { get; init; } = 4;
    public string GeneralDiagnostic { get; init; } = "Default";
    public string? MainTypeName { get; init; }
    public string? ModuleName { get; init; }
    public IReadOnlyList<string> PreprocessorSymbols { get; init; } = ImmutableArray<string>.Empty;
    public IReadOnlyDictionary<string, string> DiagnosticOptions { get; init; } = ImmutableDictionary<string, string>.Empty;
    /// <summary>Null means all host-provided references; an empty list means none.
    /// Names resolve through the host, never arbitrary file or network access.</summary>
    public IReadOnlyList<string>? ReferenceNames { get; init; }

    public CSharpCompilationSettings Normalize()
    {
        var language = Named<LanguageVersion>(LanguageVersion);
        var nullable = Named<NullableContextOptions>(Nullable);
        var optimization = Named<OptimizationLevel>(Optimization);
        var output = Named<OutputKind>(OutputKind);
        var platform = Named<Platform>(Platform);
        var metadata = Named<MetadataImportOptions>(MetadataImport);
        var documentation = Named<DocumentationMode>(DocumentationMode);
        var general = Named<ReportDiagnostic>(GeneralDiagnostic);
        if (WarningLevel is < 0 or > 9999) throw new ArgumentOutOfRangeException(nameof(WarningLevel));
        if (PreprocessorSymbols == null || PreprocessorSymbols.Count > 256 || PreprocessorSymbols.Any(symbol =>
            string.IsNullOrEmpty(symbol) || symbol.Length > 256 || !SyntaxFacts.IsValidIdentifier(symbol) || symbol.StartsWith("@", StringComparison.Ordinal)))
            throw new ArgumentException("Supply at most 256 valid conditional compilation identifiers.");
        if (DiagnosticOptions == null || DiagnosticOptions.Count > 512 || DiagnosticOptions.Keys.Any(id =>
            string.IsNullOrWhiteSpace(id) || id.Length > 64 || id.Any(character => !char.IsLetterOrDigit(character) && character != '_')))
            throw new ArgumentException("Supply at most 512 diagnostic IDs consisting of letters, digits or underscores.");
        if (ReferenceNames != null && (ReferenceNames.Count > 1024 || ReferenceNames.Any(name =>
            string.IsNullOrWhiteSpace(name) || name.Length > 1024 || name.Any(char.IsControl))))
            throw new ArgumentException("Supply at most 1024 host-provided metadata reference names.");
        if (MainTypeName?.Length > 1024 || MainTypeName?.Any(char.IsControl) == true || ModuleName?.Length > 1024 || ModuleName?.Any(char.IsControl) == true)
            throw new ArgumentException("Main type and module names must be bounded strings without control characters.");
        var result = this with
        {
            LanguageVersion = language.ToString(), Nullable = nullable.ToString(), Optimization = optimization.ToString(),
            OutputKind = output.ToString(), Platform = platform.ToString(), MetadataImport = metadata.ToString(),
            DocumentationMode = documentation.ToString(), GeneralDiagnostic = general.ToString(),
            MainTypeName = string.IsNullOrWhiteSpace(MainTypeName) ? null : MainTypeName,
            ModuleName = string.IsNullOrWhiteSpace(ModuleName) ? null : ModuleName,
            PreprocessorSymbols = PreprocessorSymbols.Distinct(StringComparer.Ordinal).OrderBy(symbol => symbol, StringComparer.Ordinal).ToImmutableArray(),
            ReferenceNames = ReferenceNames?.Distinct(StringComparer.Ordinal).OrderBy(name => name, StringComparer.Ordinal).ToImmutableArray(),
            DiagnosticOptions = DiagnosticOptions.ToImmutableSortedDictionary(pair => pair.Key, pair => Named<ReportDiagnostic>(pair.Value).ToString(), StringComparer.Ordinal)
        };
        var errors = result.CompilationOptionsCore().Errors.Where(error => error.Severity == DiagnosticSeverity.Error).ToArray();
        if (errors.Length != 0) throw new ArgumentException(string.Join("; ", errors.Select(error => error.GetMessage())));
        return result;
    }

    public CSharpParseOptions CreateParseOptions()
    {
        var normalized = Normalize();
        return new(Named<LanguageVersion>(normalized.LanguageVersion), Named<DocumentationMode>(normalized.DocumentationMode),
            SourceCodeKind.Regular, normalized.PreprocessorSymbols);
    }
    public CSharpCompilationOptions CreateCompilationOptions() => Normalize().CompilationOptionsCore();
    private CSharpCompilationOptions CompilationOptionsCore() => new CSharpCompilationOptions(Named<OutputKind>(OutputKind),
        moduleName: ModuleName, mainTypeName: MainTypeName, optimizationLevel: Named<OptimizationLevel>(Optimization),
        checkOverflow: CheckOverflow, allowUnsafe: AllowUnsafe, platform: Named<Platform>(Platform),
        generalDiagnosticOption: Named<ReportDiagnostic>(GeneralDiagnostic), warningLevel: WarningLevel,
        specificDiagnosticOptions: DiagnosticOptions.Select(pair => new KeyValuePair<string, ReportDiagnostic>(pair.Key, Named<ReportDiagnostic>(pair.Value))),
        concurrentBuild: ConcurrentBuild, deterministic: Deterministic, metadataImportOptions: Named<MetadataImportOptions>(MetadataImport),
        reportSuppressedDiagnostics: ReportSuppressedDiagnostics, nullableContextOptions: Named<NullableContextOptions>(Nullable));

    private static T Named<T>(string value) where T : struct, Enum =>
        value != null && Enum.GetNames(typeof(T)).Contains(value, StringComparer.Ordinal) && Enum.TryParse<T>(value, out var result)
            ? result : throw new ArgumentException("Use a named " + typeof(T).Name + " value.");
}
