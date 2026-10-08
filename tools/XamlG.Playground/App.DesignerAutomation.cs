using System.Collections.Immutable;
using Avalonia.Controls;
using XamlG.Automation;
using XamlG.AvaloniaRuntime.Inspection;
using XamlG.Runtime.Design;
using XamlG.Syntax;
using XamlG.Tooling;

namespace XamlG.Playground;

public partial class App
{
    private void AddDesignerAutomation()
    {
        AddAutomation<NoArguments>("designer_state", "Read design/interaction mode, grid, group selection, gesture state and independent source/runtime/designer revisions. Reports whether the trusted preview matches current project source.", AutomationScope.Designer, AutomationEffect.Read,
            (_, _) => DesignerState());
        AddAutomation<DesignerConfigureArguments>("designer_configure", "Set design mode and DIP snapping grid (zero disables snapping), or cancel a gesture. Checks designer revision and does not execute preview code or edit source.", AutomationScope.Designer, AutomationEffect.Edit,
            (args, _) =>
            {
                CheckDesignerRevision(args.ExpectedDesignerRevision);
                if (_isolationVisible && args.Enabled == true) throw new InvalidOperationException("Visual design mode requires the trusted preview.");
                if (args.GridSize is { } grid && (!double.IsFinite(grid) || grid is < 0 or > 10000)) throw new ArgumentException("Grid size must be between zero and 10000 DIPs.");
                if (args.CancelGesture) Preview.CancelGesture();
                if (args.GridSize != null) Preview.GridSize = args.GridSize.Value;
                if (args.Enabled != null) { _designerSelectionRequest++; _designMode = args.Enabled.Value; Preview.IsDesignMode = _designMode; }
                return DesignerState();
            });
        AddAutomation<DesignerSelectArguments>("designer_select", "Select a group of exact live source-owned controls, clear selection with objectIds:[], or select one current XAML path/start offset. Selection and runtime revisions are checked; path selection also requires the source revision. Does not navigate editors or execute preview code.", AutomationScope.Designer, AutomationEffect.Edit,
            (args, _) =>
            {
                CheckDesignerRevision(args.ExpectedDesignerRevision);
                if (_isolationVisible) throw new InvalidOperationException("The local designer is inactive in isolated mode.");
                var inspector = RuntimeInspector(); var snapshot = inspector.Capture();
                if (snapshot.Revision != args.ExpectedRuntimeRevision) throw new InvalidOperationException("Runtime revision changed. Refresh the designer selection.");
                if (args.ObjectIds != null)
                {
                    if (args.Path != null || args.Offset != null || args.ObjectIds.Length > 256 || args.ObjectIds.Distinct(StringComparer.Ordinal).Count() != args.ObjectIds.Length)
                        throw new ArgumentException("Select at most 256 distinct object IDs, or one path/offset.");
                    var controls = args.ObjectIds.Select(id => inspector.Resolve(id) as Control ?? throw new ArgumentException("A selected runtime object is not a control.")).ToArray();
                    if (inspector.Revision != args.ExpectedRuntimeRevision) throw new InvalidOperationException("Runtime changed while resolving selection.");
                    CheckDesignerRevision(args.ExpectedDesignerRevision); _designerSelectionRequest++; Preview.SelectControls(controls);
                }
                else
                {
                    if (args.Path == null || args.Offset == null || args.ExpectedSourceRevision == null) throw new ArgumentException("Path selection requires path, offset and expectedSourceRevision.");
                    CheckSourceRevision(args.ExpectedSourceRevision.Value); var syntax = DesignerSyntax(args.Path);
                    _designerSelectionRequest++;
                    if (!Preview.SelectSource(args.Path, args.Offset.Value, syntax.Version, notifySource: false)) throw new InvalidOperationException("No realized control matches that current XAML source location.");
                }
                return DesignerState();
            });
        AddAutomation<RuntimeHitArguments>("designer_hit_test", "Find the nearest source-owned visual at preview-root DIP coordinates. Does not alter selection.", AutomationScope.Designer, AutomationEffect.Read,
            (args, _) => { var inspector = RuntimeInspector(); var node = inspector.DesignHitTest(args.X, args.Y); return new { runtimeRevision = inspector.Revision, node }; });
        AddAutomation<DesignerTargetsArguments>("designer_targets", "Inspect root-relative DIP bounds, source locations and min/max dimensions for exact geometry targets. Rejects stale, shared-source, hidden and ancestor/descendant selections.", AutomationScope.Designer, AutomationEffect.Read,
            (args, _) => { CheckDesignerPreview(); var inspector = RuntimeInspector(); var targets = inspector.DesignTargets(args.ObjectIds, args.ExpectedRuntimeRevision); return new { sourceRevision = SourceRevision, runtimeRevision = inspector.Revision, targets }; });
        AddAutomation<DesignerGeometryArguments>("designer_geometry_plan", "Plan XAML property edits for root-relative DIP rectangles using the actual Avalonia layout policy. Checks source and runtime snapshots; no source changes or execution.", AutomationScope.Designer, AutomationEffect.Read,
            (args, _) => DesignGeometry(args, false));
        AddAutomation<DesignerGeometryArguments>("designer_geometry_apply", "Apply planned geometry as one compiler-validated XAML workspace transaction with undo. Does not reload or execute the preview; use runtime_run explicitly afterward.", AutomationScope.Designer, AutomationEffect.Edit,
            (args, _) => DesignGeometry(args, true));
        AddAutomation<DesignerArrangeArguments>("designer_arrange_plan", "Plan sibling alignment, equal sizing or equal-gap distribution. Alignment uses anchorId or the first selected object; distribution retains both endpoints and can produce overlapping gaps. Source remains unchanged.", AutomationScope.Designer, AutomationEffect.Read,
            (args, _) => DesignArrange(args, false));
        AddAutomation<DesignerArrangeArguments>("designer_arrange_apply", "Apply alignment, equal sizing or distribution as one compiler-validated source transaction. Uses the current layout policy and separate source/runtime revisions; reload is an explicit runtime operation.", AutomationScope.Designer, AutomationEffect.Edit,
            (args, _) => DesignArrange(args, true));
        Resource("xamlg://designer", "Designer state", "xamlg_designer_state");
    }
    private DesignerWorkspaceState DesignerState()
    {
        var inspector = Preview.Root == null ? null : RuntimeInspector();
        var snapshot = inspector?.Capture();
        var selected = inspector == null ? [] : Preview.SelectedControls.Select(control => inspector.DescribeValue(control).ObjectId).OfType<string>().ToArray();
        return new(SourceRevision, snapshot?.Revision, Preview.DesignRevision, Preview.Root == null ? null : Preview.Revision,
            _designMode, Preview.GridSize, Preview.IsGestureActive, _isolationVisible, Preview.Root != null && Preview.SourceRevision == SourceRevision,
            Preview.SourceRevision, selected, snapshot?.SessionId);
    }
    private void CheckDesignerRevision(long expected)
    { if (Preview.DesignRevision != expected) throw new InvalidOperationException("Designer selection or settings changed. Read designer_state again before editing."); }
    private DesignerSourcePlan DesignGeometry(DesignerGeometryArguments args, bool apply)
    {
        CheckSourceRevision(args.ExpectedSourceRevision); CheckDesignerPreview();
        var plan = RuntimeInspector().PlanDesignGeometry(args.Items, args.ExpectedRuntimeRevision, Preview.LayoutPolicy);
        return PublishDesignerPlan(plan, args.ExpectedSourceRevision, apply, "Edit visual geometry");
    }
    private DesignerSourcePlan DesignArrange(DesignerArrangeArguments args, bool apply)
    {
        CheckSourceRevision(args.ExpectedSourceRevision); CheckDesignerPreview();
        var plan = RuntimeInspector().PlanDesignArrangement(args.ObjectIds, args.Operation, args.ExpectedRuntimeRevision, args.AnchorId, Preview.LayoutPolicy);
        return PublishDesignerPlan(plan, args.ExpectedSourceRevision, apply, "Arrange visuals: " + args.Operation);
    }
    private DesignerSourcePlan PublishDesignerPlan(RuntimeDesignPlan plan, long sourceRevision, bool apply, string description)
    {
        var edits = plan.Edits.Count == 0 ? [] : XamlBatchDesignerEdits.FromVisualEdits(DesignerDocuments(), plan.Edits);
        CheckSourceRevision(sourceRevision);
        if (apply && !edits.IsEmpty)
            RestoreWorkspace(_workspaceEdits.Apply(sourceRevision, edits, description, candidate => ValidateDesignerWorkspace(candidate.Documents)));
        return new(SourceRevision, plan.Revision, plan.Geometry, plan.Edits,
            edits.Select(edit => new DesignerDocumentChanges(edit.Path, edit.Version, edit.Changes)).ToArray(), apply);
    }
    public sealed record DesignerWorkspaceState(long SourceRevision, long? RuntimeRevision, long DesignerRevision, long? PreviewRevision,
        bool Enabled, double GridSize, bool GestureActive, bool Isolated, bool PreviewMatchesSource, long? PreviewSourceRevision, IReadOnlyList<string> SelectedObjectIds, string? RuntimeSessionId = null);
    public sealed record DesignerConfigureArguments(long ExpectedDesignerRevision, bool? Enabled = null, double? GridSize = null, bool CancelGesture = false);
    public sealed record DesignerSelectArguments(long ExpectedDesignerRevision, long ExpectedRuntimeRevision, string[]? ObjectIds = null, string? Path = null, int? Offset = null, long? ExpectedSourceRevision = null);
    public sealed record DesignerTargetsArguments(string[] ObjectIds, long ExpectedRuntimeRevision);
    public sealed record DesignerGeometryArguments(RuntimeDesignGeometry[] Items, long ExpectedSourceRevision, long ExpectedRuntimeRevision);
    public sealed record DesignerArrangeArguments(string[] ObjectIds, XamlDesignArrangement Operation, long ExpectedSourceRevision, long ExpectedRuntimeRevision, string? AnchorId = null);
    public sealed record DesignerDocumentChanges(string Path, long? Version, ImmutableArray<XamlTextChange> Changes);
    public sealed record DesignerSourcePlan(long SourceRevision, long RuntimeRevision, IReadOnlyList<RuntimeDesignGeometry> Geometry,
        IReadOnlyList<XamlVisualEdit> Properties, IReadOnlyList<DesignerDocumentChanges> Documents, bool Applied);
}
