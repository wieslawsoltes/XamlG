using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.JSInterop;
using XamlG.Automation;
using XamlG.AvaloniaRuntime.Inspection;
using XamlG.Syntax;
using XamlG.Tooling;
using XamlG.Tooling.Formatting;
using XamlG.Tooling.Refactoring;

namespace XamlG.Playground;

public partial class App
{
    private AutomationCatalog _automation = default!;
    private DotNetObjectReference<App>? _automationReference;
    private AvaloniaRuntimeInspector? _runtimeInspector;
    private object? _inspectedRoot;
    private bool _sharing;
    private bool _automationVisible;
    private PermissionProfile _automationProfile = PermissionProfile.Ask;
    private AutomationReview? _automationReview;
    private TaskCompletionSource<bool>? _approval;
    private readonly SemaphoreSlim _automationGate = new(1);
    private readonly SemaphoreSlim _automationReviewGate = new(1);
    private CancellationTokenSource _automationLifetime = new();
    private readonly Dictionary<string, CancellationTokenSource> _automationRequests = new(StringComparer.Ordinal);
    private string _companionUrl = "ws://127.0.0.1:4893/bridge";
    private string _companionToken = "";
    private string _connectionStatus = "Disconnected";

    private void InitializeAutomation()
    {
        _automation = new(async (review, token) =>
        {
            await _automationReviewGate.WaitAsync(token);
            try
            {
                if (!_sharing || !_ready) return false;
                var decision = new AutomationPolicy { Profile = _automationProfile }.Decide(review.Tool);
                if (decision != PermissionDecision.Ask) return decision == PermissionDecision.Allow;
                _automationReview = review;
                _approval = new(TaskCreationOptions.RunContinuationsAsynchronously);
                StateHasChanged();
                try
                {
                    var allowed = await _approval.Task.WaitAsync(token);
                    if (allowed) await CaptureEditorsAsync();
                    return allowed && _sharing;
                }
                finally { _automationReview = null; _approval = null; StateHasChanged(); }
            }
            finally { _automationReviewGate.Release(); }
        });
        AddAutomation<NoArguments>("capabilities", "Discover the current IDE tool catalog and protocol limits.", AutomationScope.Project, AutomationEffect.Read,
            (_, _) => new { revision = SourceRevision, tools = _automation.Tools, limits = new { sourceOffsets = "UTF-16", maxRead = 262144 }, runtime = Preview.Root != null });
        AddAutomation<NoArguments>("project_get", "Read project document inventory, revisions and undo/redo availability.", AutomationScope.Project, AutomationEffect.Read,
            (_, _) => new { revision = SourceRevision, documents = WorkspaceTexts().Select(p => new { path = p.Key, length = p.Value.Length, language = p.Key.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ? "csharp" : "xaml" }), canUndo = _workspaceEdits.CanUndo, canRedo = _workspaceEdits.CanRedo });
        AddAutomation<DocumentRead>("document_read", "Read a bounded UTF-16 source range from the actual editor buffers.", AutomationScope.Source, AutomationEffect.Read,
            (args, _) =>
            {
                var text = Source(args.Path); Range(args.Offset, args.Count, text.Length);
                var end = Math.Min(text.Length, args.Offset + args.Count);
                return new { revision = SourceRevision, args.Path, args.Offset, total = text.Length, text = text[args.Offset..end], hasMore = end < text.Length };
            });
        AddAutomation<SearchArguments>("source_search", "Search project source with literal ordinal matching and bounded results.", AutomationScope.Source, AutomationEffect.Read,
            (args, _) =>
            {
                if (string.IsNullOrEmpty(args.Query) || args.Limit is < 1 or > 1000) throw new ArgumentException("Supply a nonempty query and limit 1–1000.");
                var result = new List<object>(); var truncated = false;
                foreach (var (path, text) in WorkspaceTexts().Where(p => args.Path == null || p.Key == args.Path))
                    for (var start = 0; start <= text.Length - args.Query.Length;)
                    {
                        var index = text.IndexOf(args.Query, start, args.MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
                        if (index < 0) break;
                        if (result.Count == args.Limit) { truncated = true; break; }
                        result.Add(new { path, start = index, length = args.Query.Length }); start = index + args.Query.Length;
                    }
                return new { revision = SourceRevision, matches = result, truncated };
            });
        AddAutomation<SourceEdits>("source_edit", "Atomically edit multiple source ranges with revision and optional expected-text checks. One normal undo step.", AutomationScope.Source, AutomationEffect.Edit,
            (args, context) =>
            {
                CheckSourceRevision(args.ExpectedRevision);
                var edits = args.Edits.GroupBy(e => e.Path).Select(group =>
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
                }).ToArray();
                RestoreWorkspace(_workspaceEdits.Apply(args.ExpectedRevision, edits, context.Caller + ": edit source", candidate => ValidateWorkspace(candidate.Documents)));
                return new { revision = SourceRevision };
            });
        AddAutomation<DocumentWrite>("document_write", "Create or replace a C# or XAML project source document. Requires the current project revision.", AutomationScope.Source, AutomationEffect.Edit,
            (args, context) =>
            {
                CheckSourceRevision(args.ExpectedRevision); ValidateDocumentPath(args.Path);
                var texts = WorkspaceTexts(); texts[args.Path] = args.Text;
                ValidateWorkspace(texts);
                RestoreWorkspace(_workspaceEdits.ReplaceAll(args.ExpectedRevision, texts, context.Caller + ": write " + args.Path));
                return new { revision = SourceRevision };
            });
        AddAutomation<DocumentRemove>("document_remove", "Remove an auxiliary project document in one undo step.", AutomationScope.Project, AutomationEffect.Edit,
            (args, context) =>
            {
                CheckSourceRevision(args.ExpectedRevision);
                if (args.Path is "View.axaml" or "Code.cs") throw new ArgumentException("The main source documents cannot be removed.");
                var texts = WorkspaceTexts(); if (!texts.Remove(args.Path)) throw new KeyNotFoundException("Unknown document.");
                RestoreWorkspace(_workspaceEdits.ReplaceAll(args.ExpectedRevision, texts, context.Caller + ": remove " + args.Path));
                return new { revision = SourceRevision };
            });
        AddAutomation<RevisionArguments>("project_undo", "Undo the last normal project source transaction.", AutomationScope.Project, AutomationEffect.Edit,
            (args, _) => { CheckSourceRevision(args.ExpectedRevision); RestoreWorkspace(_workspaceEdits.Undo(args.ExpectedRevision)); return new { revision = SourceRevision }; });
        AddAutomation<RevisionArguments>("project_redo", "Redo the last normal project source transaction.", AutomationScope.Project, AutomationEffect.Edit,
            (args, _) => { CheckSourceRevision(args.ExpectedRevision); RestoreWorkspace(_workspaceEdits.Redo(args.ExpectedRevision)); return new { revision = SourceRevision }; });
        AddAutomation<NoArguments>("project_export", "Export the current editable project as inert JSON, without a download dialog or execution.", AutomationScope.Project, AutomationEffect.Read,
            (_, _) => new { revision = SourceRevision, format = "xamlg-project", version = 3, xaml = _document.Current.Text, code = _code, resources = ResourceTexts(), codeFiles = CodeTexts(), documents = WorkspaceTexts() });
        AddAutomation<RestoreSourceArguments>("project_restore", "Selectively restore reviewed source changes in one undo transaction. Checks the current revision and every expected file before applying.", AutomationScope.Source, AutomationEffect.Edit,
            (args, context) =>
            {
                CheckSourceRevision(args.ExpectedRevision);
                if (args.Files.Length is < 1 or > 1024 || args.Files.Select(f => f.Path).Distinct(StringComparer.Ordinal).Count() != args.Files.Length)
                    throw new ArgumentException("Select distinct source files.");
                var texts = WorkspaceTexts();
                foreach (var file in args.Files)
                {
                    ValidateDocumentPath(file.Path);
                    if (texts.GetValueOrDefault(file.Path) != file.After) throw new AutomationException("revision_conflict", "The reviewed file has changed: " + file.Path);
                    if (file.Before == null)
                    {
                        if (file.Path is "View.axaml" or "Code.cs") throw new ArgumentException("The main documents cannot be removed.");
                        texts.Remove(file.Path);
                    }
                    else texts[file.Path] = file.Before;
                }
                ValidateWorkspace(texts);
                RestoreWorkspace(_workspaceEdits.ReplaceAll(args.ExpectedRevision, texts, context.Caller + ": restore reviewed source"));
                return new { revision = SourceRevision, documents = WorkspaceTexts() };
            });
        AddAutomation<NoArguments>("compiler_compile", "Compile the current XAML project and C# using XamlG and Roslyn. Does not execute application code.", AutomationScope.Compiler, AutomationEffect.Read,
            (_, context) => { var result = AnalyzeAutomation(context.CancellationToken); return new { revision = SourceRevision, result.Success, result.Diagnostics, result.ElapsedMilliseconds }; });
        AddAutomation<NoArguments>("compiler_references", "Read the actual Roslyn metadata references and compilation options.", AutomationScope.Compiler, AutomationEffect.Read,
            (_, context) =>
            {
                var result = AnalyzeAutomation(context.CancellationToken);
                return new { revision = SourceRevision, references = result.Compilation.References.Select(r => r.Display), outputKind = result.Compilation.Options.OutputKind.ToString(), result.Compilation.Options.AllowUnsafe, languageVersion = ((CSharpParseOptions)result.Compilation.SyntaxTrees.First().Options).LanguageVersion.ToString() };
            });
        AddAutomation<NoArguments>("generated_list", "List all generated C# files, including project loader adapters and resource factories.", AutomationScope.Compiler, AutomationEffect.Read,
            (_, context) =>
            {
                var result = AnalyzeAutomation(context.CancellationToken);
                return new { revision = SourceRevision, files = result.Compilation.SyntaxTrees.Where(t => !result.SourcePaths.Contains(t.FilePath)).Select(t => new { path = t.FilePath, length = t.Length }) };
            });
        AddAutomation<DocumentRead>("generated_read", "Read a bounded range of an exact generated C# file.", AutomationScope.Compiler, AutomationEffect.Read,
            (args, context) =>
            {
                var tree = CSharpTree(args.Path, context.CancellationToken); var text = tree.ToString(); Range(args.Offset, args.Count, text.Length);
                return new { revision = SourceRevision, args.Path, args.Offset, total = text.Length, text = text.Substring(args.Offset, Math.Min(args.Count, text.Length - args.Offset)) };
            });
        AddAutomation<PathArguments>("xaml_syntax", "Inspect source-preserving XAML syntax nodes and source ranges.", AutomationScope.Compiler, AutomationEffect.Read,
            (args, context) => new { revision = SourceRevision, tree = XamlInspector.Syntax(XamlAnalysisFor(args.Path, context.CancellationToken).Syntax) });
        AddAutomation<PathArguments>("xaml_bound", "Inspect the real XamlG typed operation tree.", AutomationScope.Compiler, AutomationEffect.Read,
            (args, context) => new { revision = SourceRevision, tree = XamlInspector.Bound(XamlAnalysisFor(args.Path, context.CancellationToken).Document) });
        AddAutomation<PositionArguments>("xaml_hover", "Read Roslyn-backed symbol hover for XAML at a UTF-16 offset.", AutomationScope.Source, AutomationEffect.Read,
            (args, context) => { var analysis = XamlAnalysisFor(args.Path, context.CancellationToken); return new { revision = SourceRevision, hover = new XamlLanguageService(_result!.AuthoringCompiler!).GetHover(analysis, args.Offset, context.CancellationToken) }; });
        AddAutomation<PositionArguments>("xaml_definitions", "Locate declarations for the XAML symbol at the requested source offset.", AutomationScope.Source, AutomationEffect.Read,
            (args, context) => { var analysis = XamlAnalysisFor(args.Path, context.CancellationToken); return new { revision = SourceRevision, definitions = new XamlLanguageService(_result!.AuthoringCompiler!).GetDefinitions(analysis, args.Offset) }; });
        AddAutomation<XamlCompleteArguments>("xaml_complete", "Get Avalonia XAML element, attribute or value completions from real Roslyn symbols.", AutomationScope.Source, AutomationEffect.Read,
            (args, context) => { var analysis = XamlAnalysisFor(args.Path, context.CancellationToken); return new { revision = SourceRevision, completions = new XamlLanguageService(_result!.AuthoringCompiler!).GetCompletions(analysis, args.Offset, args.Kind, context.CancellationToken) }; });
        AddAutomation<FormatArguments>("xaml_format", "Format an XAML document through the shared authoring service with normal undo.", AutomationScope.Source, AutomationEffect.Edit,
            (args, context) =>
            {
                CheckSourceRevision(args.ExpectedRevision); var analysis = XamlAnalysisFor(args.Path, context.CancellationToken);
                var edits = XamlFormatter.Format(analysis.Syntax, new() { TabSize = 2 }, analysis: analysis);
                RestoreWorkspace(_workspaceEdits.Apply(args.ExpectedRevision, [new(args.Path, analysis.Syntax.Text, analysis.Syntax.Version, edits)], context.Caller + ": format", candidate => ValidateWorkspace(candidate.Documents)));
                return new { revision = SourceRevision };
            });
        AddAutomation<RenameArguments>("xaml_rename", "Semantically rename an XAML name and its XAML/C# references atomically.", AutomationScope.Source, AutomationEffect.Edit,
            (args, context) =>
            {
                CheckSourceRevision(args.ExpectedRevision); var analysis = XamlAnalysisFor(args.Path, context.CancellationToken);
                var plan = new XamlRenameService(_result!.AuthoringCompiler!).Rename(analysis, args.Offset, args.Name,
                    _result.Project!.Documents.Select(d => new XamlAnalysis(d.Input.Syntax, d.Document, d.Output)));
                RestoreWorkspace(_workspaceEdits.Apply(args.ExpectedRevision, plan.Documents, context.Caller + ": rename", candidate => ValidateWorkspace(candidate.Documents)));
                return new { revision = SourceRevision, plan.OldName, plan.NewName };
            });
        AddAutomation<CSharpSyntaxArguments>("csharp_syntax", "Inspect Roslyn syntax nodes/tokens for source or generated C#, with bounded depth and count.", AutomationScope.Compiler, AutomationEffect.Read,
            (args, context) =>
            {
                if (args.MaxDepth is < 0 or > 32 || args.MaxNodes is < 1 or > 10000) throw new ArgumentException("Invalid tree bounds.");
                var tree = CSharpTree(args.Path, context.CancellationToken); var root = tree.GetRoot(context.CancellationToken);
                if (args.Offset < 0 || args.Offset > root.FullSpan.End) throw new ArgumentException("Offset out of bounds.");
                var start = args.Offset == 0 ? root : root.FindToken(args.Offset).Parent!; var count = 0; var truncated = false;
                object? Visit(SyntaxNodeOrToken node, int depth)
                {
                    if (count >= args.MaxNodes) { truncated = true; return null; }
                    count++; context.CancellationToken.ThrowIfCancellationRequested();
                    return new { kind = node.Kind().ToString(), node.IsToken, start = node.Span.Start, length = node.Span.Length,
                        text = node.IsToken ? node.ToString() : null, children = depth < args.MaxDepth && node.IsNode
                            ? node.AsNode()!.ChildNodesAndTokens().Select(child => Visit(child, depth + 1)).Where(child => child != null).ToArray() : [] };
                }
                var result = Visit(start, 0);
                return new { revision = SourceRevision, tree = result, count, truncated };
            });
        AddAutomation<PositionArguments>("csharp_symbol", "Inspect the Roslyn symbol, type, constant and declaration locations at a C# source offset.", AutomationScope.Compiler, AutomationEffect.Read,
            (args, context) =>
            {
                var symbol = CSharpLanguage(context.CancellationToken).GetSymbol(args.Path, args.Offset, context.CancellationToken);
                return new { revision = SourceRevision, symbol = symbol?.Display, kind = symbol?.Kind, type = symbol?.Type,
                    hasConstant = symbol?.HasConstant ?? false, constant = symbol?.Constant, locations = symbol?.Locations };
            });
        AddCSharpAutomation();
        AddDesignerAutomation();
        AddAutomation<DesignerEditArguments>("designer_edit", "Edit XAML properties or structure without executing the preview. Uses revision-checked source transactions and undo.", AutomationScope.Designer, AutomationEffect.Edit,
            (args, context) =>
            {
                CheckSourceRevision(args.ExpectedRevision); var text = Source(args.Path); var tree = XamlSyntaxTree.Parse(text, args.Path);
                var element = tree.Root?.DescendantsAndSelf().SingleOrDefault(e => e.Span.Start == args.Offset) ?? throw new ArgumentException("Select an exact element start offset.");
                var transaction = args.Operation switch
                {
                    DesignerOperation.SetProperty => XamlDesignerEdits.SetProperty(tree, element, args.Name ?? throw new ArgumentException("Property name required."), args.Value ?? ""),
                    DesignerOperation.RemoveProperty => XamlDesignerEdits.RemoveProperty(tree, element, args.Name ?? throw new ArgumentException("Property name required.")),
                    DesignerOperation.Insert => XamlDesignerEdits.InsertChild(tree, element, args.Value ?? throw new ArgumentException("Markup required."), args.Index),
                    DesignerOperation.Remove => XamlDesignerEdits.RemoveElement(tree, element),
                    DesignerOperation.RenameType => XamlDesignerEdits.RenameElement(tree, element, args.Name ?? throw new ArgumentException("Type name required.")),
                    DesignerOperation.Reparent => XamlDesignerEdits.Reparent(tree, element, tree.Root!.DescendantsAndSelf().Single(e => e.Span.Start == args.ParentOffset), args.Index),
                    _ => throw new ArgumentException("Unknown designer operation.")
                };
                if (tree.WithChanges(transaction.Changes, tree.Version).HasErrors) throw new ArgumentException("The edit would produce malformed XAML.");
                RestoreWorkspace(_workspaceEdits.Apply(args.ExpectedRevision, [new(args.Path, text, null, transaction.Changes)], context.Caller + ": " + transaction.Description, candidate => ValidateWorkspace(candidate.Documents)));
                return new { revision = SourceRevision };
            });
        AddAutomation<RuntimeTreeArguments>("runtime_tree", "Inspect all actual Avalonia visual/logical nodes with stable handles, parent/child relations, bounds, classes, data context and source provenance.", AutomationScope.Runtime, AutomationEffect.Read,
            (_, _) => RuntimeInspector().Capture());
        AddAutomation<RuntimeObjectArguments>("runtime_properties", "Read registered, attached, direct and CLR properties with effective values, value priority, read-only and animation information.", AutomationScope.Runtime, AutomationEffect.Read,
            (args, _) => { var runtime = RuntimeInspector(); var properties = runtime.Properties(args.ObjectId); return new { revision = runtime.Revision, properties }; });
        AddAutomation<RuntimeSetArguments>("runtime_property_set", "Set a live Avalonia property using an invariant typed literal. This changes the running object, not source.", AutomationScope.Runtime, AutomationEffect.Execute,
            (args, _) => { var runtime = RuntimeInspector(); var property = runtime.SetProperty(args.ObjectId, args.Property, args.Value, args.ExpectedRevision); return new { runtime.Revision, property }; });
        AddAutomation<RuntimeClearArguments>("runtime_property_clear", "Clear a live Avalonia local property value, revealing styles/defaults again.", AutomationScope.Runtime, AutomationEffect.Execute,
            (args, _) => { var runtime = RuntimeInspector(); runtime.ClearProperty(args.ObjectId, args.Property, args.ExpectedRevision); return new { runtime.Revision }; });
        AddAutomation<RuntimeClassesArguments>("runtime_classes_set", "Replace user classes on a live styled element while preserving framework pseudo classes.", AutomationScope.Runtime, AutomationEffect.Execute,
            (args, _) => { var runtime = RuntimeInspector(); runtime.SetClasses(args.ObjectId, args.Classes, args.ExpectedRevision); return new { runtime.Revision }; });
        AddAutomation<RuntimeObjectArguments>("runtime_resources", "Inspect resources declared on a live styled element.", AutomationScope.Runtime, AutomationEffect.Read,
            (args, _) => new { resources = RuntimeInspector().Resources(args.ObjectId) });
        AddAutomation<RuntimeResourceArguments>("runtime_resource_set", "Create, replace or remove a live local resource using a typed literal.", AutomationScope.Runtime, AutomationEffect.Execute,
            (args, _) => { var runtime = RuntimeInspector(); runtime.SetResource(args.ObjectId, args.Key, args.Value, args.Type, args.Remove, args.ExpectedRevision); return new { runtime.Revision }; });
        AddAutomation<RuntimeObjectArguments>("runtime_events", "List registered routed events for a live runtime object.", AutomationScope.Runtime, AutomationEffect.Read,
            (args, _) => new { events = RuntimeInspector().Events(args.ObjectId) });
        AddAutomation<RuntimeEventArguments>("runtime_event_raise", "Raise a supported no-payload routed event on a live control, including Button.Click.", AutomationScope.Runtime, AutomationEffect.Execute,
            (args, _) => { var runtime = RuntimeInspector(); runtime.RaiseEvent(args.ObjectId, args.Event, args.ExpectedRevision); return new { runtime.Revision }; });
        AddAutomation<RuntimeMutationArguments>("runtime_focus", "Focus a running Avalonia input element.", AutomationScope.Runtime, AutomationEffect.Execute,
            (args, _) => new { focused = RuntimeInspector().Focus(args.ObjectId, args.ExpectedRevision) });
        AddAutomation<RuntimeMutationArguments>("runtime_layout", "Invalidate and update real Avalonia measure/arrange for a running control.", AutomationScope.Runtime, AutomationEffect.Execute,
            (args, _) => { var runtime = RuntimeInspector(); runtime.UpdateLayout(args.ObjectId, args.ExpectedRevision); return new { runtime.Revision }; });
        AddAutomation<RuntimeMutationArguments>("runtime_bring_into_view", "Bring a running control into view through Avalonia's routed request.", AutomationScope.Runtime, AutomationEffect.Execute,
            (args, _) => { var runtime = RuntimeInspector(); runtime.BringIntoView(args.ObjectId, args.ExpectedRevision); return new { runtime.Revision }; });
        AddAutomation<RuntimeHitArguments>("runtime_hit_test", "Hit test the actual Avalonia preview using root-relative coordinates.", AutomationScope.Runtime, AutomationEffect.Read,
            (args, _) => new { objectId = RuntimeInspector().HitTest(args.X, args.Y) });
        AddAutomation<RuntimeChangesArguments>("runtime_changes", "Read bounded property/tree/event changes after a sequence, including a history-loss indicator.", AutomationScope.Runtime, AutomationEffect.Read,
            (args, _) => RuntimeInspector().Changes(args.AfterSequence));
        AddRuntimeObjectAutomation();
        _automation.Add<RevisionArguments, object>("xamlg_runtime_run", "Compile and run the current trusted preview. Executes application code.", AutomationScope.Runtime, AutomationEffect.Execute,
            async (args, context) =>
            {
                CheckSourceRevision(args.ExpectedRevision); var result = AnalyzeAutomation(context.CancellationToken);
                _visualTree = await ShowTrustedCompilationAsync(result); _previewShown = true; _isolationVisible = false;
                return RuntimeInspector().Capture();
            });
        _automation.Add<NoArguments, object>("xamlg_layout_get", "Read the real Dockyard workspace layout.", AutomationScope.Layout, AutomationEffect.Read,
            async (_, _) => new { layout = await _dock.SaveLayoutAsync() });
        _automation.Add<LayoutSetArguments, object>("xamlg_layout_set", "Restore a validated Dockyard layout with the current pane registry.", AutomationScope.Layout, AutomationEffect.Edit,
            async (args, _) => { await _dock.LoadLayoutAsync(args.Layout); return new { layout = await _dock.SaveLayoutAsync() }; });
        _automation.Add<NoArguments, object>("xamlg_layout_reset", "Reset the Dockyard workspace to its default layout.", AutomationScope.Layout, AutomationEffect.Edit,
            async (_, _) => { await ResetDockyardAsync(); return new { layout = await _dock.SaveLayoutAsync() }; });
        _automation.Add<LayoutContentArguments, object>("xamlg_layout_content", "Activate, float, dock, hide or show an existing Dockyard pane.", AutomationScope.Layout, AutomationEffect.Edit,
            async (args, _) =>
            {
                if (args.ContentId is not ("source" or "explorer" or "preview" or "inspector" or "problems" or "agent")) throw new ArgumentException("Unknown pane.");
                await using var item = await _dock.FindAsync(args.ContentId);
                // Dockyard.Blazor 0.2.2 exposes Float (an in-page floating window).
                // FloatInPage is a newer JavaScript convenience method, absent in this pin.
                if (args.Operation == LayoutOperation.FloatInPage)
                { await using var floating = await _dock.InvokeAsync<IJSObjectReference>("Float", item); }
                else if (!await _dock.InvokeAsync<bool>(args.Operation.ToString(), item))
                    throw new InvalidOperationException("Dockyard rejected this operation for the selected pane.");
                return new { layout = await _dock.SaveLayoutAsync() };
            });

        Resource("xamlg://project", "Project", "xamlg_project_get");
        Resource("xamlg://capabilities", "Capabilities", "xamlg_capabilities");
        Resource("xamlg://diagnostics", "Diagnostics", "xamlg_compiler_compile");
        Resource("xamlg://generated", "Generated C# files", "xamlg_generated_list");
        AddAutomationResources();
        AddBuildAutomation();
        _automation.AddPrompt(new("repair", "Inspect and repair current compilation errors", "Read xamlg_project_get and xamlg_compiler_compile, inspect relevant current source, make revision-checked edits, and compile again. Report the actual diagnostic evidence."));
        _automation.AddPrompt(new("inspect-runtime", "Inspect the actual Avalonia preview", "Read xamlg_runtime_tree, inspect properties of relevant live handles, and correlate source provenance with XAML. Runtime revisions and source revisions are independent. Do not execute or mutate without permission."));
    }

    private void AddAutomation<T>(string suffix, string description, AutomationScope scope, AutomationEffect effect, Func<T, AutomationCallContext, object> execute) =>
        _automation.Add<T, object>("xamlg_" + suffix, description, scope, effect, (args, context) => ValueTask.FromResult(execute(args, context)));
    private void Resource(string uri, string name, string tool) => _automation.AddResource(new(uri, name, name),
        async context => (await _automation.CallAsync(tool, AutomationJson.Element(new NoArguments()), context)).GetRawText());
    private long SourceRevision => _workspaceEdits.Current.Revision;
    private string Source(string path) => WorkspaceTexts().TryGetValue(path, out var text) ? text : throw new KeyNotFoundException("Unknown source document.");
    private void CheckSourceRevision(long expected)
    {
        if (expected != SourceRevision) throw new AutomationException("revision_conflict", $"Expected source revision {expected}, actual {SourceRevision}. Read current source before editing.");
    }
    private static void Range(int offset, int count, int length)
    { if (offset < 0 || offset > length || count is < 1 or > 262144) throw new ArgumentException("Invalid source range. Count must be 1–262144 UTF-16 units."); }
    private static void ValidateDocumentPath(string path)
    {
        if (path is "View.axaml" or "Code.cs") return;
        var normalized = IsCSharpPath(path) ? CSharpProjectDocumentStore.NormalizePath(path) : XamlProjectDocumentStore.NormalizePath(path);
        if (path != normalized) throw new ArgumentException("Use a normalized project-relative C# or XAML path.");
    }
    private BrowserCompilation AnalyzeAutomation(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (_result != null && ReferenceEquals(_result.Analysis.Syntax, _document.Current) && _result.CodeText == _code &&
            _result.ResourceRevision == Compiler.Resources.Revision && _result.CodeRevision == Compiler.CodeFiles.Revision) return _result;
        _result = Compiler.Analyze(_document.Current, _code, cancellationToken: token);
        NotifyCompilerResources();
        return _result;
    }
    private XamlAnalysis XamlAnalysisFor(string path, CancellationToken token)
    {
        var document = AnalyzeAutomation(token).Project!.Documents.SingleOrDefault(d => d.Input.LogicalPath == path)
            ?? throw new KeyNotFoundException("Unknown XAML document.");
        return new(document.Input.Syntax, document.Document, document.Output);
    }
    private SyntaxTree CSharpTree(string path, CancellationToken token) => AnalyzeAutomation(token).Compilation.SyntaxTrees
        .SingleOrDefault(t => t.FilePath == path) ?? throw new KeyNotFoundException("Unknown C# source or generated file.");
    private AvaloniaRuntimeInspector RuntimeInspector()
    {
        var root = Preview.Root ?? throw new InvalidOperationException("Run a preview before inspecting live objects.");
        if (!ReferenceEquals(root, _inspectedRoot))
        { _runtimeInspector?.Dispose(); _runtimeInspector = new(root); _runtimeInspector.RuntimeChanged += NotifyRuntimeResource; _inspectedRoot = root; }
        return _runtimeInspector!;
    }
    [JSInvokable]
    public JsonElement AutomationCatalog() => AutomationJson.Element(new { tools = _automation.Tools, resources = _automation.Resources, prompts = _automation.Prompts });
    [JSInvokable]
    public async Task<JsonElement> AutomationInvoke(string id, string method, string name, JsonElement arguments, string caller, string principalId)
    {
        if (!_sharing) throw new AutomationException("unavailable", "Agent access is disabled.");
        if (string.IsNullOrWhiteSpace(caller) || caller.Length > 200) throw new ArgumentException("Invalid automation caller label.");
        if (string.IsNullOrWhiteSpace(principalId) || principalId.Length > 512) throw new ArgumentException("Invalid transport principal.");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_automationLifetime.Token);
        if (!_automationRequests.TryAdd(id, cancellation)) throw new AutomationException("duplicate_request", "Duplicate automation request.");
        var entered = false;
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        _automationActivity.Add(method, name, caller, "started", 0);
        try
        {
            if (method == "call" && name == "xamlg_wait")
            {
                var waited = await _automation.CallAsync(name, arguments, new(caller, cancellation.Token, principalId));
                _automationActivity.Add(method, name, caller, "completed", System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                return waited;
            }
            await _automationGate.WaitAsync(cancellation.Token); entered = true;
            if (!_sharing || !_ready || _busy) throw new AutomationException("unavailable", "Enable agent access and wait for the current IDE operation.");
            await CaptureEditorsAsync(); // Include edits still queued in Monaco before validating revisions.
            var context = new AutomationCallContext(caller, cancellation.Token, principalId);
            var result = method switch
            {
                "call" => await _automation.CallAsync(name, arguments, context),
                "resource" => AutomationJson.Element(await _automation.ReadResourceAsync(name, context)),
                "complete" => AutomationJson.Element(await _automation.CompleteAsync(name, arguments.GetProperty("argument").GetString()!, arguments.GetProperty("value").GetString()!, context)),
                _ => throw new AutomationException("unknown_method", "Unknown automation method.")
            };
            await SaveDraftAsync();
            _automationActivity.Add(method, name, caller, "completed", System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            StateHasChanged(); return result;
        }
        // JS interop must receive an explicit failed invocation for cancellation, including
        // callers queued behind another tool when a lease is revoked.
        catch (OperationCanceledException)
        { _automationActivity.Add(method, name, caller, "cancelled", System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds); throw new AutomationException("cancelled", "The operation was cancelled or access revoked."); }
        catch (Exception error)
        { _automationActivity.Add(method, name, caller, "failed", System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds, error is AutomationException automation ? automation.Code : error.GetType().Name); throw; }
        finally { _automationRequests.Remove(id); if (entered) _automationGate.Release(); StateHasChanged(); }
    }
    [JSInvokable]
    public void AutomationCancel(string id) { if (_automationRequests.TryGetValue(id, out var request)) request.Cancel(); }
    private void RevokeAutomation()
    {
        _sharing = false; _approval?.TrySetResult(false);
        _automationLifetime.Cancel();
        _buildArtifacts.Clear();
    }
    private async Task SetAutomationSharing(Microsoft.AspNetCore.Components.ChangeEventArgs args)
    {
        if (args.Value is true)
        {
            if (_automationLifetime.IsCancellationRequested) { _automationLifetime.Dispose(); _automationLifetime = new(); }
            _sharing = true;
        }
        else await DisconnectAutomationAsync();
    }
    private async Task ConnectAutomationAsync()
    {
        if (_module == null) return;
        try { await _module.InvokeVoidAsync("connectAutomation", _companionUrl, _companionToken); _companionToken = ""; _connectionStatus = "Connected"; }
        catch (Exception error) { _connectionStatus = error.Message; }
    }
    private async Task DisconnectAutomationAsync()
    {
        RevokeAutomation();
        if (_module != null) await _module.InvokeVoidAsync("disconnectAutomation");
        _companionToken = ""; _connectionStatus = "Disconnected";
    }
    private async Task RetireAutomationWorkspaceAsync()
    {
        await DisconnectAutomationAsync();
        // Let cancelled operations leave the old project before installing its replacement.
        await _automationGate.WaitAsync();
        _automationGate.Release();
    }
    private void ApproveAutomation(bool allow) => _approval?.TrySetResult(allow);

    public sealed record NoArguments;
    public sealed record DocumentRead(string Path, int Offset = 0, int Count = 65536);
    public sealed record PathArguments(string Path);
    public sealed record PositionArguments(string Path, int Offset);
    public sealed record SearchArguments(string Query, string? Path = null, bool MatchCase = false, int Limit = 100);
    public sealed record SourceEdit(string Path, int Start, int Length, string Text, string? ExpectedText = null);
    public sealed record SourceEdits(long ExpectedRevision, SourceEdit[] Edits);
    public sealed record RevisionArguments(long ExpectedRevision);
    public sealed record DocumentWrite(string Path, string Text, long ExpectedRevision);
    public sealed record DocumentRemove(string Path, long ExpectedRevision);
    public sealed record SourceRestoreFile(string Path, string? Before, string? After);
    public sealed record RestoreSourceArguments(long ExpectedRevision, SourceRestoreFile[] Files);
    public sealed record FormatArguments(string Path, long ExpectedRevision);
    public sealed record RenameArguments(string Path, int Offset, string Name, long ExpectedRevision);
    public sealed record XamlCompleteArguments(string Path, int Offset, XamlCompletionKind Kind);
    public sealed record CSharpSyntaxArguments(string Path, int Offset = 0, int MaxDepth = 8, int MaxNodes = 1000);
    public enum DesignerOperation { SetProperty, RemoveProperty, Insert, Remove, RenameType, Reparent }
    public sealed record DesignerEditArguments(string Path, int Offset, DesignerOperation Operation, long ExpectedRevision,
        string? Name = null, string? Value = null, int ParentOffset = -1, int Index = -1);
    public sealed record RuntimeTreeArguments;
    public sealed record RuntimeObjectArguments(string ObjectId);
    public sealed record RuntimeSetArguments(string ObjectId, string Property, JsonElement Value, long ExpectedRevision);
    public sealed record RuntimeClearArguments(string ObjectId, string Property, long ExpectedRevision);
    public sealed record RuntimeClassesArguments(string ObjectId, string[] Classes, long ExpectedRevision);
    public sealed record RuntimeResourceArguments(string ObjectId, string Key, JsonElement Value, long ExpectedRevision, string? Type = null, bool Remove = false);
    public sealed record RuntimeEventArguments(string ObjectId, string Event, long ExpectedRevision);
    public sealed record RuntimeMutationArguments(string ObjectId, long ExpectedRevision);
    public sealed record RuntimeHitArguments(double X, double Y);
    public sealed record RuntimeChangesArguments(long AfterSequence = 0);
    public sealed record LayoutSetArguments(string Layout);
    public enum LayoutOperation { Activate, FloatInPage, Dock, Hide, Show }
    public sealed record LayoutContentArguments(string ContentId, LayoutOperation Operation);
}
