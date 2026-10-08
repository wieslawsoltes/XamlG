using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using XamlG.Automation;
using XamlG.Tooling;

namespace XamlG.Playground;

public partial class App
{
    public const string CompilerSettingsPath = "CompilerSettings.json";
    private static readonly JsonSerializerOptions CompilerSettingsJson = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true, PropertyNameCaseInsensitive = false, DictionaryKeyPolicy = null,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, NumberHandling = JsonNumberHandling.Strict,
        MaxDepth = 16
    };
    private static string DefaultCompilerSettingsText => SerializeCompilerSettings(new());
    private string _compilerSettingsText = DefaultCompilerSettingsText;
    private static string SerializeCompilerSettings(CSharpCompilationSettings settings) => JsonSerializer.Serialize(settings.Normalize(), CompilerSettingsJson);
    private static CSharpCompilationSettings ParseCompilerSettings(string text)
    {
        if (text.Length > 65536) throw new ArgumentException("CompilerSettings.json exceeds its 64 KiB character limit.");
        using var document = JsonDocument.Parse(text, new() { MaxDepth = 16 });
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new ArgumentException("Compiler settings must be a JSON object.");
        CheckNames(document.RootElement);
        return (JsonSerializer.Deserialize<CSharpCompilationSettings>(text, CompilerSettingsJson) ?? throw new ArgumentException("Compiler settings cannot be null.")).Normalize();
        static void CheckNames(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                { if (!names.Add(property.Name)) throw new ArgumentException("Duplicate compiler setting key: " + property.Name); CheckNames(property.Value); }
            }
            else if (element.ValueKind == JsonValueKind.Array) foreach (var item in element.EnumerateArray()) CheckNames(item);
        }
    }
    private void ResetCompilerSettings()
    { _compilerSettingsText = DefaultCompilerSettingsText; Compiler.SetSettings(new()); }
    private bool IsCompilationCurrent(BrowserCompilation? result) => result != null && ReferenceEquals(result.Analysis.Syntax, _document.Current) &&
        result.CodeText == _code && result.ResourceRevision == Compiler.Resources.Revision && result.CodeRevision == Compiler.CodeFiles.Revision && result.SettingsRevision == Compiler.SettingsRevision;

    private void AddCompilerAutomation()
    {
        AddAutomation<NoArguments>("compiler_options_get", "Read the editable Roslyn compiler settings, their workspace JSON document, valid named options and available host metadata references. Works even when the current project cannot compile.", AutomationScope.Compiler, AutomationEffect.Read,
            (_, _) => CompilerOptionsState());
        AddAutomation<CompilerOptionsArguments>("compiler_options_set", "Replace validated Roslyn settings in CompilerSettings.json as one project undo transaction. Covers parsing, symbols, nullable/unsafe/overflow, diagnostics, output and available metadata selection. Does not compile or run code.", AutomationScope.Compiler, AutomationEffect.Edit,
            (args, context) =>
            {
                CheckSourceRevision(args.ExpectedRevision);
                var text = SerializeCompilerSettings(Compiler.ValidateSettings(args.Options));
                var documents = WorkspaceTexts(); documents[CompilerSettingsPath] = text; ValidateWorkspace(documents);
                RestoreWorkspace(_workspaceEdits.ReplaceAll(args.ExpectedRevision, documents, context.Caller + ": configure compiler"));
                return CompilerOptionsState();
            });
        AddAutomation<CompilerOptionsTextArguments>("compiler_options_write", "Validate and save the complete compiler settings JSON document, preserving its text in workspace history. Rejects unknown/duplicate keys and unknown metadata names before publication.", AutomationScope.Compiler, AutomationEffect.Edit,
            (args, context) =>
            {
                CheckSourceRevision(args.ExpectedRevision);
                var documents = WorkspaceTexts(); documents[CompilerSettingsPath] = args.Text; ValidateWorkspace(documents);
                RestoreWorkspace(_workspaceEdits.ReplaceAll(args.ExpectedRevision, documents, context.Caller + ": edit compiler settings"));
                return CompilerOptionsState();
            });
        AddAutomation<NoArguments>("compiler_snapshot", "Inspect the actual Roslyn compilation identity, source/generated syntax trees, active assembly/module references and effective options. Does not execute code.", AutomationScope.Compiler, AutomationEffect.Read,
            (_, context) =>
            {
                var result = AnalyzeAutomation(context.CancellationToken);
                return new { revision = SourceRevision, result.SettingsRevision, result.Compilation.AssemblyName,
                    result.Settings, result.Success, result.Diagnostics,
                    trees = result.Compilation.SyntaxTrees.Select(tree => new { path = tree.FilePath, tree.Length, generated = !result.SourcePaths.Contains(tree.FilePath),
                        languageVersion = ((CSharpParseOptions)tree.Options).LanguageVersion.ToString() }),
                    references = result.Compilation.References.Select(reference => new { name = reference.Display, kind = reference.Properties.Kind.ToString(),
                        reference.Properties.EmbedInteropTypes, reference.Properties.Aliases,
                        identity = result.Compilation.GetAssemblyOrModuleSymbol(reference) is IAssemblySymbol assembly ? assembly.Identity.ToString() : result.Compilation.GetAssemblyOrModuleSymbol(reference)?.ToDisplayString() }) };
            });
        AddAutomation<CSharpMoveArguments>("csharp_file_move", "Move an auxiliary C# source document to an unused project-relative .cs path in one revision-checked undo step. Preserves its source text; types are not renamed. Code.cs is owned by the studio.", AutomationScope.Source, AutomationEffect.Edit,
            (args, context) =>
            {
                CheckSourceRevision(args.ExpectedRevision);
                var oldPath = CSharpProjectDocumentStore.NormalizePath(args.Path); var newPath = CSharpProjectDocumentStore.NormalizePath(args.NewPath);
                if (oldPath != args.Path || newPath != args.NewPath || oldPath == "Code.cs" || newPath == "Code.cs") throw new ArgumentException("Use normalized auxiliary C# paths.");
                var documents = WorkspaceTexts();
                if (!documents.TryGetValue(oldPath, out var source)) throw new KeyNotFoundException("Unknown C# source document.");
                if (documents.ContainsKey(newPath)) throw new ArgumentException("The destination path is already in use.");
                documents.Remove(oldPath); documents.Add(newPath, source); ValidateWorkspace(documents);
                RestoreWorkspace(_workspaceEdits.ReplaceAll(args.ExpectedRevision, documents, context.Caller + ": move C# file"));
                return new { revision = SourceRevision, oldPath, newPath };
            });
        Resource("xamlg://compiler/options", "Compiler options", "xamlg_compiler_options_get");
    }
    private CompilerOptionsSnapshot CompilerOptionsState() => new(SourceRevision, Compiler.SettingsRevision, Compiler.Settings, _compilerSettingsText,
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["languageVersion"] = Enum.GetNames<LanguageVersion>(), ["nullable"] = Enum.GetNames<NullableContextOptions>(),
            ["optimization"] = Enum.GetNames<OptimizationLevel>(), ["outputKind"] = Enum.GetNames<OutputKind>(),
            ["platform"] = Enum.GetNames<Platform>(), ["metadataImport"] = Enum.GetNames<MetadataImportOptions>(),
            ["documentationMode"] = Enum.GetNames<DocumentationMode>(), ["generalDiagnostic"] = Enum.GetNames<ReportDiagnostic>()
        }, Compiler.AvailableReferenceNames);
    private async Task<JsonElement> ExecuteCompilerUiAsync(string name, JsonElement arguments, CancellationToken cancellationToken)
    {
        var tool = _automation.Tools.SingleOrDefault(tool => tool.Name == name && tool.Scope == AutomationScope.Compiler)
            ?? throw new ArgumentException("Select a compiler operation.");
        await _automationGate.WaitAsync(cancellationToken);
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            if (!_ready || _busy || _disposed) throw new InvalidOperationException("Wait for the current IDE operation.");
            await CaptureEditorsAsync();
            if (!_ready || _busy || _disposed) throw new InvalidOperationException("The IDE operation was superseded.");
            _automationActivity.Add("call", name, "Studio owner", "started", 0);
            var result = await _automation.CallLocalAsync(name, arguments, new("Studio owner", cancellationToken, "studio-owner"));
            if (tool.Effect == AutomationEffect.Edit) { await SaveDraftAsync(); ScheduleAutomaticUpdate(); }
            _automationActivity.Add("call", name, "Studio owner", "completed", System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            return result;
        }
        catch (Exception error)
        {
            _automationActivity.Add("call", name, "Studio owner", error is OperationCanceledException ? "cancelled" : "failed",
                System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds, error is AutomationException automation ? automation.Code : error.GetType().Name);
            throw;
        }
        finally { _automationGate.Release(); if (!_disposed) StateHasChanged(); }
    }
    public sealed record CompilerOptionsArguments(CSharpCompilationSettings Options, long ExpectedRevision);
    public sealed record CompilerOptionsTextArguments(string Text, long ExpectedRevision);
    public sealed record CSharpMoveArguments(string Path, string NewPath, long ExpectedRevision);
    public sealed record CompilerOptionsSnapshot(long Revision, long SettingsRevision, CSharpCompilationSettings Options, string Text,
        IReadOnlyDictionary<string, string[]> Choices, IReadOnlyList<string> AvailableReferences);
}
