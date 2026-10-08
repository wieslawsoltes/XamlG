using System.Reflection;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Xunit;

namespace XamlG.Tooling.Tests;

public sealed class CSharpCompilationSettingsTests
{
    [Fact]
    public void Normalization_copies_mutable_collections_and_preserves_all_versus_no_reference_selection()
    {
        var symbols = new List<string> { "TRACE", "FEATURE", "TRACE" };
        var references = new List<string> { "System.Runtime.dll", "Avalonia.Base.dll", "System.Runtime.dll" };
        var diagnostics = new Dictionary<string, string> { ["CS8603"] = "Error", ["CS0168"] = "Suppress" };
        var normalized = new CSharpCompilationSettings
        {
            PreprocessorSymbols = symbols, ReferenceNames = references, DiagnosticOptions = diagnostics, MainTypeName = " ", ModuleName = " "
        }.Normalize();
        var snapshot = JsonSerializer.Serialize(normalized);
        symbols.Clear(); references[0] = "changed"; diagnostics["CS8603"] = "Suppress";
        Assert.Equal(snapshot, JsonSerializer.Serialize(normalized));
        Assert.Equal(["FEATURE", "TRACE"], normalized.PreprocessorSymbols);
        Assert.Equal(["Avalonia.Base.dll", "System.Runtime.dll"], normalized.ReferenceNames);
        Assert.Equal(["CS0168", "CS8603"], normalized.DiagnosticOptions.Keys);
        Assert.Null(normalized.MainTypeName); Assert.Null(normalized.ModuleName);
        Assert.Equal(snapshot, JsonSerializer.Serialize(normalized.Normalize()));
        Assert.Null(new CSharpCompilationSettings().Normalize().ReferenceNames);
        Assert.Empty(new CSharpCompilationSettings { ReferenceNames = [] }.Normalize().ReferenceNames!);
    }

    [Fact]
    public void Compiler_settings_reject_invalid_names_unbounded_collections_and_control_characters()
    {
        CSharpCompilationSettings[] invalid =
        [
            new() { LanguageVersion = "preview" }, new() { LanguageVersion = "999" }, new() { LanguageVersion = null! },
            new() { Nullable = "enable" }, new() { Optimization = "Fast" }, new() { OutputKind = "Exe" },
            new() { Platform = "Unknown" }, new() { MetadataImport = "-1" }, new() { DocumentationMode = "Unknown" },
            new() { GeneralDiagnostic = "Ignore" }, new() { WarningLevel = -1 }, new() { WarningLevel = 10000 },
            new() { PreprocessorSymbols = ["@DEBUG"] }, new() { PreprocessorSymbols = ["invalid-symbol"] },
            new() { PreprocessorSymbols = [new string('x', 257)] }, new() { PreprocessorSymbols = Enumerable.Repeat("DEBUG", 257).ToArray() },
            new() { PreprocessorSymbols = null! }, new() { DiagnosticOptions = null! },
            new() { DiagnosticOptions = new Dictionary<string, string> { ["CS8603"] = "error" } },
            new() { DiagnosticOptions = new Dictionary<string, string> { ["bad id"] = "Error" } },
            new() { DiagnosticOptions = Enumerable.Range(0, 513).ToDictionary(index => "CS" + index, _ => "Default") },
            new() { ReferenceNames = [" "] }, new() { ReferenceNames = ["System\nRuntime"] },
            new() { ReferenceNames = [new string('x', 1025)] }, new() { ReferenceNames = Enumerable.Repeat("System.Runtime.dll", 1025).ToArray() },
            new() { MainTypeName = "Probe\nType" }, new() { ModuleName = new string('x', 1025) }
        ];
        foreach (var settings in invalid) Assert.ThrowsAny<ArgumentException>(() => settings.Normalize());
    }

    [Fact]
    public void Conditional_symbols_and_language_version_change_the_actual_compilation()
    {
        const string code = "#if FEATURE\npublic record Enabled(int Number);\n#else\npublic class Disabled {}\n#endif";
        var disabled = new CSharpTestProject(("Code.cs", code)); disabled.AssertCompiles();
        Assert.NotNull(disabled.Compilation.GetTypeByMetadataName("Disabled"));
        Assert.Null(disabled.Compilation.GetTypeByMetadataName("Enabled"));
        var settings = new CSharpCompilationSettings { PreprocessorSymbols = ["FEATURE"], LanguageVersion = "CSharp9" };
        var enabled = new CSharpTestProject(settings, ("Code.cs", code)); enabled.AssertCompiles();
        Assert.True(enabled.Compilation.GetTypeByMetadataName("Enabled")!.IsRecord);
        Assert.Null(enabled.Compilation.GetTypeByMetadataName("Disabled"));
        var older = new CSharpTestProject(settings with { LanguageVersion = "CSharp8" }, ("Code.cs", code));
        Assert.Contains(older.Compilation.GetDiagnostics(), diagnostic => diagnostic.Id == "CS8400" && diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.Null(older.Compilation.GetTypeByMetadataName("Enabled"));
    }

    [Fact]
    public void Nullable_and_diagnostic_policies_change_reported_severity_and_suppression()
    {
        const string code = "public class Probe { public string Read() => null; }";
        var settings = new CSharpCompilationSettings();
        Assert.Equal(DiagnosticSeverity.Warning, NullableDiagnostic(settings).Severity);
        Assert.Empty(new CSharpTestProject(settings with { Nullable = "Disable" }, ("Code.cs", code)).Compilation.GetDiagnostics());
        var elevated = NullableDiagnostic(settings with { DiagnosticOptions = new Dictionary<string, string> { ["CS8603"] = "Error" } });
        Assert.Equal(DiagnosticSeverity.Error, elevated.Severity); Assert.True(elevated.IsWarningAsError);
        var suppressed = settings with { DiagnosticOptions = new Dictionary<string, string> { ["CS8603"] = "Suppress" } };
        Assert.Empty(new CSharpTestProject(suppressed, ("Code.cs", code)).Compilation.GetDiagnostics());
        // An option-disabled diagnostic is not computed. Roslyn's suppressed report
        // exposes diagnostics suppressed within source, for example by a pragma.
        Assert.Empty(new CSharpTestProject(suppressed with { ReportSuppressedDiagnostics = true }, ("Code.cs", code)).Compilation.GetDiagnostics());
        var pragma = new CSharpTestProject(settings with { ReportSuppressedDiagnostics = true }, ("Code.cs", "#pragma warning disable CS8603\n" + code));
        Assert.True(Assert.Single(pragma.Compilation.GetDiagnostics(), diagnostic => diagnostic.Id == "CS8603").IsSuppressed);
        Assert.Equal(DiagnosticSeverity.Error, NullableDiagnostic(settings with { GeneralDiagnostic = "Error" }).Severity);
        Diagnostic NullableDiagnostic(CSharpCompilationSettings options) => Assert.Single(new CSharpTestProject(options, ("Code.cs", code)).Compilation.GetDiagnostics(), diagnostic => diagnostic.Id == "CS8603");
    }

    [Fact]
    public void Overflow_and_unsafe_options_change_compiled_behavior_and_errors()
    {
        const string code = "public class Probe { public static int Run() { int value = int.MaxValue; return value + 1; } }";
        Assert.Equal(int.MinValue, new CSharpTestProject(("Code.cs", code)).Run());
        var checkedProject = new CSharpTestProject(new CSharpCompilationSettings { CheckOverflow = true }, ("Code.cs", code));
        var error = Assert.Throws<TargetInvocationException>(() => checkedProject.Run());
        Assert.IsType<OverflowException>(error.InnerException);
        const string unsafeCode = "public unsafe class Probe { public static int Read(int* value) => *value; }";
        new CSharpTestProject(("Code.cs", unsafeCode)).AssertCompiles();
        var safeOnly = new CSharpTestProject(new CSharpCompilationSettings { AllowUnsafe = false }, ("Code.cs", unsafeCode));
        Assert.Contains(safeOnly.Compilation.GetDiagnostics(), diagnostic => diagnostic.Id == "CS0227" && diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void Emission_honors_entry_point_platform_module_and_deterministic_settings()
    {
        const string code = "class First { static int Main() => 1; } class Second { static int Main() => 2; }";
        var settings = new CSharpCompilationSettings
        {
            OutputKind = "ConsoleApplication", MainTypeName = "Second", ModuleName = "Probe.exe", Platform = "X64", Deterministic = true
        };
        var first = new CSharpTestProject(settings, ("Code.cs", code)); first.AssertCompiles();
        Assert.Equal("Second", first.Compilation.GetEntryPoint(default)!.ContainingType.Name);
        Assert.Equal("Probe.exe", first.Compilation.SourceModule.Name);
        var image = Emit(first);
        Assert.Equal(image, Emit(new CSharpTestProject(settings, ("Code.cs", code))));
        using var pe = new PEReader(new MemoryStream(image));
        Assert.Equal(Machine.Amd64, pe.PEHeaders.CoffHeader.Machine);
        Assert.False(pe.PEHeaders.CoffHeader.Characteristics.HasFlag(Characteristics.Dll));
        var ambiguous = new CSharpTestProject(settings with { MainTypeName = null }, ("Code.cs", code));
        Assert.Contains(ambiguous.Compilation.GetDiagnostics(), diagnostic => diagnostic.Id == "CS0017");
        static byte[] Emit(CSharpTestProject project)
        {
            using var stream = new MemoryStream(); var result = project.Compilation.Emit(stream);
            Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
            return stream.ToArray();
        }
    }
}
