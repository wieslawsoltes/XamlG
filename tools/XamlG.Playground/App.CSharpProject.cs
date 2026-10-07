using XamlG.Playground.Components;
using XamlG.Tooling;

namespace XamlG.Playground;

public partial class App
{
    private ProjectCode? _projectCodeEditor;
    private static bool IsCSharpPath(string path) => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);
    private Dictionary<string, string> CodeTexts() => Compiler.CodeFiles.Snapshot.ToDictionary(p => p.Key, p => p.Value.Text, StringComparer.Ordinal);
    private void ValidateWorkspace(IReadOnlyDictionary<string, string> documents)
    {
        if (!documents.ContainsKey("View.axaml") || !documents.ContainsKey("Code.cs") || !documents.ContainsKey(CompilerSettingsPath)) throw new ArgumentException("The main XAML, C# and compiler settings documents are required.");
        Compiler.ValidateSettings(ParseCompilerSettings(documents[CompilerSettingsPath]));
        foreach (var path in documents.Keys) ValidateDocumentPath(path);
        new XamlProjectDocumentStore(["View.axaml"]).ReplaceAll(documents.Where(p => p.Key != "View.axaml" && p.Key != CompilerSettingsPath && !IsCSharpPath(p.Key)).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal));
        new CSharpProjectDocumentStore(["Code.cs"]).ReplaceAll(documents.Where(p => p.Key != "Code.cs" && IsCSharpPath(p.Key)).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal));
    }
    private async Task ProjectCodeChangedAsync()
    {
        _result = null; _status = "C# source changed · compile to update XAML and Roslyn analysis";
        await SaveDraftAsync();
    }
}
