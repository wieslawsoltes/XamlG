using Microsoft.JSInterop;
using XamlG.ProjectSystem;

namespace XamlG.Playground.Workspaces;

public static class SolutionArchiveInterop
{
    [JSInvokable]
    public static async Task<ImportedWorkspaceFile[]> ImportSolutionArchive(byte[] bytes)
    {
        if (bytes.Length > WorkspaceArchive.MaximumArchiveBytes) throw new ArgumentException("Workspace archive limit exceeded.");
        using var stream = new MemoryStream(bytes, writable: false);
        var workspace = await WorkspaceArchive.ImportAsync(stream);
        return workspace.Current.Files.Select(pair => new ImportedWorkspaceFile(pair.Key, pair.Value.Content, pair.Value.IsBinary)).ToArray();
    }

    [JSInvokable]
    public static Task<byte[]> ExportSolutionArchive(ImportedWorkspaceFile[] files)
    {
        var workspace = new VirtualWorkspace(files.Select(file => KeyValuePair.Create(file.Path, new WorkspaceFile(file.Content, file.IsBinary))));
        return WorkspaceArchive.ExportAsync(workspace.Current);
    }
}
