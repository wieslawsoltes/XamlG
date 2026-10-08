using Microsoft.AspNetCore.Components;
using XamlG.Syntax;
using XamlG.Tooling;

namespace XamlG.Playground;

public partial class App
{
    private string _inspectionPath = "View.axaml";
    private IEnumerable<string> InspectionPaths => new[] { "View.axaml" }.Concat(Compiler.Resources.Snapshot.Keys.Order(StringComparer.Ordinal));
    private XamlAnalysis? InspectionAnalysis
    {
        get
        {
            if (!InspectionPaths.Contains(_inspectionPath)) _inspectionPath = "View.axaml";
            if (_inspectionPath == "View.axaml") return _result?.Analysis;
            var document = _result?.Project?.Documents.FirstOrDefault(document => document.Input.LogicalPath == _inspectionPath);
            return document == null ? null : new(document.Input.Syntax, document.Document, document.Output);
        }
    }
    private Task OpenResourceSyntaxAsync(string path) { _inspectionPath = path; return ShowPaneAsync("syntax"); }
    private void InspectionPathChanged(ChangeEventArgs args) => _inspectionPath = args.Value?.ToString() ?? "View.axaml";
}
