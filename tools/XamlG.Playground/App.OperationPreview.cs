using System.Collections.Immutable;
using System.Text.Json;
using XamlG.Agents;
using XamlG.Automation;
using XamlG.Syntax;
using XamlG.Tooling;
using XamlG.Tooling.Editing;
using XamlG.Tooling.Formatting;
using XamlG.Tooling.Refactoring;

namespace XamlG.Playground;

public partial class App
{
    private void AddOperationPreviewAutomation() => AddAutomation<OperationPreviewArguments>("operation_preview",
        "Preview the exact before/after source for a registered source mutation without applying it. Runtime and layout operations return effect metadata only.",
        AutomationScope.Source, AutomationEffect.Read, (args, context) => PreviewOperation(args, context.CancellationToken));

    private object PreviewOperation(OperationPreviewArguments request, CancellationToken token)
    {
        var tool = _automation.Tools.SingleOrDefault(tool => tool.Name == request.Name) ?? throw new ArgumentException("Unknown operation.");
        AutomationSchema.Validate(tool.InputSchema, request.Arguments);
        var before = WorkspaceTexts(); var after = new Dictionary<string, string>(before, StringComparer.Ordinal);
        var staging = new XamlWorkspaceEditSession(before);
        T Args<T>() => request.Arguments.Deserialize<T>(AutomationJson.Options) ?? throw new ArgumentException("Arguments required.");
        void Edit(IEnumerable<XamlDocumentEdits> edits) => after = staging.Apply(staging.Current.Revision, edits, "Preview operation").Documents.ToDictionary();
        void Revision(long expected) => CheckSourceRevision(expected);
        var source = true;
        switch (request.Name)
        {
            case "xamlg_source_edit":
                var edits = Args<SourceEdits>(); Revision(edits.ExpectedRevision);
                Edit(edits.Edits.GroupBy(edit => edit.Path).Select(group =>
                {
                    var text = Source(group.Key);
                    var changes = group.Select(edit =>
                    {
                        if (edit.Start < 0 || edit.Length < 0 || edit.Start > text.Length || edit.Length > text.Length - edit.Start)
                            throw new ArgumentException("Source range is out of bounds.");
                        if (edit.ExpectedText != null && text.Substring(edit.Start, edit.Length) != edit.ExpectedText)
                            throw new AutomationException("revision_conflict", "Expected text no longer matches.");
                        return new XamlTextChange(new(edit.Start, edit.Length), edit.Text);
                    }).ToImmutableArray();
                    return new XamlDocumentEdits(group.Key, text, null, changes);
                })); break;
            case "xamlg_document_write":
                var write = Args<DocumentWrite>(); Revision(write.ExpectedRevision); ValidateDocumentPath(write.Path); after[write.Path] = write.Text; break;
            case "xamlg_document_remove":
                var remove = Args<DocumentRemove>(); Revision(remove.ExpectedRevision);
                if (remove.Path is "View.axaml" or "Code.cs" or CompilerSettingsPath) throw new ArgumentException("Main documents cannot be removed.");
                if (!after.Remove(remove.Path)) throw new KeyNotFoundException("Unknown source document."); break;
            case "xamlg_project_restore":
                var restore = Args<RestoreSourceArguments>(); Revision(restore.ExpectedRevision);
                if (restore.Files.Length is < 1 or > 1024 || restore.Files.Select(file => file.Path).Distinct(StringComparer.Ordinal).Count() != restore.Files.Length)
                    throw new ArgumentException("Select distinct source files.");
                foreach (var file in restore.Files)
                {
                    ValidateDocumentPath(file.Path);
                    if (before.GetValueOrDefault(file.Path) != file.After) throw new AutomationException("revision_conflict", "The reviewed file changed: " + file.Path);
                    if (file.Before == null) after.Remove(file.Path); else after[file.Path] = file.Before;
                }
                break;
            case "xamlg_project_undo":
            case "xamlg_project_redo":
                var history = Args<RevisionArguments>(); Revision(history.ExpectedRevision);
                after = _workspaceEdits.PreviewHistory(history.ExpectedRevision, request.Name == "xamlg_project_undo").Documents.ToDictionary(); break;
            case "xamlg_compiler_options_set":
                var options = Args<CompilerOptionsArguments>(); Revision(options.ExpectedRevision);
                after[CompilerSettingsPath] = SerializeCompilerSettings(Compiler.ValidateSettings(options.Options)); break;
            case "xamlg_compiler_options_write":
                var settings = Args<CompilerOptionsTextArguments>(); Revision(settings.ExpectedRevision); after[CompilerSettingsPath] = settings.Text; break;
            case "xamlg_csharp_file_move":
                var move = Args<CSharpMoveArguments>(); Revision(move.ExpectedRevision);
                if (move.Path == "Code.cs" || !Compiler.CodeFiles.Snapshot.ContainsKey(move.Path)) throw new ArgumentException("Select an auxiliary C# document.");
                var target = CSharpProjectDocumentStore.NormalizePath(move.NewPath);
                if (after.ContainsKey(target)) throw new ArgumentException("Choose an unused C# path.");
                after[target] = after[move.Path]; after.Remove(move.Path); break;
            case "xamlg_xaml_format":
            case "xamlg_csharp_format":
                var format = Args<FormatArguments>(); Revision(format.ExpectedRevision);
                if (request.Name == "xamlg_csharp_format") Edit([new(format.Path, Source(format.Path), null, CSharpLanguage(token).Format(format.Path, cancellationToken: token))]);
                else
                {
                    var analysis = XamlAnalysisFor(format.Path, token);
                    Edit([new(format.Path, analysis.Syntax.Text, analysis.Syntax.Version, XamlFormatter.Format(analysis.Syntax, new() { TabSize = 2 }, analysis: analysis))]);
                }
                break;
            case "xamlg_csharp_action_apply":
                var action = Args<CSharpActionArguments>(); Revision(action.ExpectedRevision);
                var plan = CSharpLanguage(token).GetActions(action.Path, action.Offset, token).SingleOrDefault(candidate => candidate.Title == action.Title)
                    ?? throw new ArgumentException("This source action is no longer available.");
                Edit([new(action.Path, Source(action.Path), null, plan.Changes)]); break;
            case "xamlg_xaml_rename":
            case "xamlg_csharp_rename":
                var rename = Args<RenameArguments>(); Revision(rename.ExpectedRevision);
                if (request.Name == "xamlg_xaml_rename") XamlAnalysisFor(rename.Path, token); else CSharpLanguage(token);
                Edit(ProjectRename().Rename(rename.Path, rename.Offset, rename.Name, token).Documents); break;
            case "xamlg_designer_edit":
                var designer = Args<DesignerEditArguments>(); Revision(designer.ExpectedRevision);
                var tree = DesignerSyntax(designer.Path);
                var transaction = PlanDesignerEdit(tree, designer);
                if (tree.WithChanges(transaction.Changes, tree.Version).HasErrors) throw new ArgumentException("The edit would produce malformed XAML.");
                Edit([new(designer.Path, tree.Text, null, transaction.Changes)]); break;
            case "xamlg_designer_geometry_apply":
            case "xamlg_designer_arrange_apply":
                var design = request.Name == "xamlg_designer_geometry_apply" ? DesignGeometry(Args<DesignerGeometryArguments>(), false) : DesignArrange(Args<DesignerArrangeArguments>(), false);
                Edit(design.Documents.Select(document => new XamlDocumentEdits(document.Path, Source(document.Path), document.Version, document.Changes)));
                ValidateDesignerWorkspace(after); break;
            default: source = false; break;
        }
        token.ThrowIfCancellationRequested();
        if (source) ValidateWorkspace(after);
        var files = before.Keys.Union(after.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal)
            .Where(path => before.GetValueOrDefault(path) != after.GetValueOrDefault(path))
            .Select(path => new AgentFileChange(path, before.GetValueOrDefault(path), after.GetValueOrDefault(path))).ToArray();
        return new { revision = SourceRevision, sourcePreview = source, files,
            note = source ? "Exact source preview at this revision. Execution rechecks revisions; approval does not make stale edits valid." :
                "This operation changes runtime, selection or layout state, or reads existing state. Review its arguments and declared effects; it has no source replacement preview." };
    }
    private static XamlEditTransaction PlanDesignerEdit(XamlSyntaxTree tree, DesignerEditArguments args)
    {
        var element = tree.FindElement(args.Offset) ?? throw new ArgumentException("Select an XAML element.");
        return args.Operation switch
        {
            DesignerOperation.SetProperty => XamlDesignerEdits.SetProperty(tree, element, args.Name ?? throw new ArgumentException("Property name required."), args.Value ?? ""),
            DesignerOperation.RemoveProperty => XamlDesignerEdits.RemoveProperty(tree, element, args.Name ?? throw new ArgumentException("Property name required.")),
            DesignerOperation.Insert => XamlDesignerEdits.InsertChild(tree, element, args.Value ?? throw new ArgumentException("Markup required."), args.Index),
            DesignerOperation.Remove => XamlDesignerEdits.RemoveElement(tree, element),
            DesignerOperation.RenameType => XamlDesignerEdits.RenameElement(tree, element, args.Name ?? throw new ArgumentException("Type name required.")),
            DesignerOperation.Reparent => XamlDesignerEdits.Reparent(tree, element, tree.Root!.DescendantsAndSelf().Single(candidate => candidate.Span.Start == args.ParentOffset), args.Index),
            _ => throw new ArgumentException("Unknown designer operation.")
        };
    }
    public sealed record OperationPreviewArguments(string Name, JsonElement Arguments);
}
