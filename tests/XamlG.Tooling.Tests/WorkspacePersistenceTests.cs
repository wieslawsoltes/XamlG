using XamlG.Tooling.Editing;
using Xunit;

namespace XamlG.Tooling.Tests;

public sealed class WorkspacePersistenceTests
{
    [Fact]
    public void Restore_preserves_source_and_both_sides_of_undo_history()
    {
        var session = new XamlWorkspaceEditSession(new Dictionary<string, string> { ["View.axaml"] = "one" });
        session.ReplaceAll(0, new Dictionary<string, string> { ["View.axaml"] = "two", ["Model.cs"] = "class Model {}" }, "Add model");
        session.ReplaceAll(1, new Dictionary<string, string> { ["View.axaml"] = "three" }, "Remove model");
        session.Undo(2);
        var restored = new XamlWorkspaceEditSession([]);
        restored.RestoreState(session.CaptureState());
        Assert.Equal("two", restored.Current.Documents["View.axaml"]);
        Assert.True(restored.CanUndo); Assert.True(restored.CanRedo);
        restored.Redo(restored.Current.Revision); Assert.False(restored.Current.Documents.ContainsKey("Model.cs"));
        restored.Undo(restored.Current.Revision); restored.Undo(restored.Current.Revision);
        Assert.Equal("one", restored.Current.Documents["View.axaml"]);
    }
}
