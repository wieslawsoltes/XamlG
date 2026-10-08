using System.Diagnostics;
using System.Text;
using XamlG.Agents;
using Xunit;

namespace XamlG.Automation.Tests;

public sealed class AgentSourceReviewTests
{
    [Fact]
    public void Selective_restore_preserves_other_changes_and_exact_utf16_line_endings()
    {
        const string before = "head 😀\r\nold first\r\nsame\nold last";
        const string after = "head 😀\r\nnew first\r\nsame\nnew last\n";
        var file = new AgentFileChange("View.axaml", before, after);
        var diff = AgentSourceReview.Diff(file);
        Assert.True(diff.SelectiveRestoreAvailable); Assert.False(diff.Coarse); Assert.False(diff.Truncated);
        Assert.Equal(2, diff.Blocks.Count);
        var first = AgentSourceReview.PreviewBlock(file, diff.Blocks[0].Id);
        Assert.Equal("old first\r\n", first.Before); Assert.Equal("new first\r\n", first.After);
        Assert.Equal("head 😀\r\n".Length, first.Block.BeforeStart);
        var restored = AgentSourceReview.RestoreBlock(file, first.Block.Id);
        Assert.Equal(after, restored.After);
        Assert.Equal("head 😀\r\nold first\r\nsame\nnew last\n", restored.Before);
        var last = AgentSourceReview.PreviewBlock(file, diff.Blocks[1].Id);
        Assert.Equal("old last", last.Before); Assert.Equal("new last\n", last.After);
        Assert.Equal("head 😀\r\nnew first\r\nsame\nold last", AgentSourceReview.RestoreBlock(file, last.Block.Id).Before);
        Assert.Equal(["CRLF", "CRLF", "CRLF", "LF", "none", "LF"], diff.Lines.Select(line => line.LineEnding));
    }

    [Theory]
    [InlineData("", "added\n", "", "added\n")]
    [InlineData("removed\n", "", "removed\n", "")]
    [InlineData("same\na\nsame\nb\nsame", "same\nA\nsame\nB\nsame", "a\n", "A\n")]
    [InlineData("a\r\nb\n", "a\nb\n", "a\r\n", "a\n")]
    [InlineData("a\n", "a", "a\n", "a")]
    public void Insertions_deletions_repeated_lines_and_newline_only_changes_are_exact(
        string before, string after, string removed, string added)
    {
        var file = new AgentFileChange("Text.cs", before, after);
        var blocks = AgentSourceReview.Diff(file).Blocks;
        var preview = AgentSourceReview.PreviewBlock(file, blocks[0].Id);
        Assert.Equal(removed, preview.Before); Assert.Equal(added, preview.After);
        var current = after;
        // Applying independent reviewed blocks from right to left reconstructs the original
        // bytes without moving the offsets of earlier changes.
        foreach (var block in blocks.Reverse())
            current = current.Remove(block.AfterStart, block.AfterLength).Insert(block.AfterStart,
                AgentSourceReview.PreviewBlock(file, block.Id).Before);
        Assert.Equal(before, current);
    }

    [Theory]
    [InlineData(null, "created")]
    [InlineData("deleted", null)]
    public void Added_and_deleted_documents_require_whole_file_restore(string? before, string? after)
    {
        var file = new AgentFileChange("Text.cs", before, after);
        var diff = AgentSourceReview.Diff(file);
        Assert.False(diff.SelectiveRestoreAvailable);
        Assert.Throws<InvalidOperationException>(() => AgentSourceReview.RestoreBlock(file, diff.Blocks[0].Id));
    }

    [Fact]
    public void Diff_pages_keep_complete_stable_block_metadata_and_original_line_numbers()
    {
        var prefix = string.Concat(Enumerable.Range(1, 50).Select(i => $"line {i}\n"));
        var file = new AgentFileChange("Text.cs", prefix + "old\nlast", prefix + "new\nlast");
        var all = AgentSourceReview.Diff(file);
        var pages = Enumerable.Range(0, all.TotalRows).Select(i => AgentSourceReview.Diff(file, 1, i)).ToArray();
        Assert.Equal(all.Lines, pages.SelectMany(page => page.Lines));
        Assert.Equal(48, all.Lines[0].BeforeLine);
        Assert.All(pages, page => { Assert.Equal(all.Blocks, page.Blocks); Assert.True(page.Truncated); });
        Assert.Empty(AgentSourceReview.Diff(file, 1, all.TotalRows).Lines);
        Assert.Throws<ArgumentOutOfRangeException>(() => AgentSourceReview.Diff(file, 1, all.TotalRows + 1));
        Assert.Throws<ArgumentException>(() => AgentSourceReview.RestoreBlock(file, "not-a-block"));
    }

    [Fact]
    public void Coarse_diff_still_restores_the_exact_middle_and_bounds_block_previews()
    {
        var before = "prefix\n" + string.Concat(Enumerable.Range(0, 600).Select(i => $"old {i} {new string('a', 40)}\r\n")) + "suffix";
        var after = "prefix\n" + string.Concat(Enumerable.Range(0, 600).Select(i => $"new {i} {new string('b', 40)}\r\n")) + "suffix";
        var file = new AgentFileChange("Text.cs", before, after);
        var diff = AgentSourceReview.Diff(file, 10000);
        Assert.True(diff.Coarse); Assert.True(diff.SelectiveRestoreAvailable);
        var block = Assert.Single(diff.Blocks);
        var preview = AgentSourceReview.PreviewBlock(file, block.Id);
        Assert.True(preview.Truncated); Assert.True(preview.Coarse);
        Assert.Equal(20000, preview.Before.Length); Assert.Equal(20000, preview.After.Length);
        Assert.Equal(before, AgentSourceReview.RestoreBlock(file, block.Id).Before);
    }

    [Theory]
    [InlineData(200, 6000)]
    [InlineData(21000, 300)]
    public void Display_text_has_an_aggregate_budget_including_oversized_documents(int lines, int width)
    {
        var before = string.Concat(Enumerable.Repeat(new string('a', width) + "\n", lines));
        var after = string.Concat(Enumerable.Repeat(new string('b', width) + "\n", lines));
        var diff = AgentSourceReview.Diff(new("Large.cs", before, after), 10000);
        Assert.True(diff.Truncated);
        Assert.InRange(diff.Lines.Sum(line => line.Text.Length), 1, 1_000_000);
        if (lines > 20000)
        {
            Assert.True(diff.Coarse);
            Assert.Empty(diff.Blocks); Assert.False(diff.SelectiveRestoreAvailable);
            Assert.Throws<InvalidOperationException>(() => AgentSourceReview.PreviewBlock(new("Large.cs", before, after), "change-1"));
        }
    }

    [Fact]
    public void File_identities_ignore_other_documents_but_distinguish_absence_and_exact_utf16()
    {
        var file = new AgentFileChange("View.axaml", "old\r\n", "new\r\n");
        var first = new AgentChangeReview(1, [file]);
        var next = new AgentChangeReview(2, [file, new("Other.cs", "old", "new")]);
        Assert.Equal(first.FileIdentities[file.Path], next.FileIdentities[file.Path]);
        Assert.NotEqual(first.ReviewId, next.ReviewId); Assert.True(next.ReviewVersion > first.ReviewVersion);
        string Identity(string? before, string? after) => new AgentChangeReview(1, [new("x", before, after)]).FileIdentities["x"];
        Assert.NotEqual(Identity(null, ""), Identity("", ""));
        Assert.NotEqual(Identity("ab", "c"), Identity("a", "bc"));
        Assert.NotEqual(Identity("\ud800", ""), Identity("\ud801", ""));
        Assert.NotEqual(Identity("\r\n", ""), Identity("\n", ""));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Exported_patch_applies_and_reverses_exact_source_including_empty_files(bool emptyDocuments)
    {
        AgentFileChange[] files = emptyDocuments ?
            [new("created.cs", null, ""), new("deleted.cs", "", null)] :
            [new("View space.axaml", "old 😀\r\nlast", "new 😀\r\nlast\r\n"),
             new(OperatingSystem.IsWindows() ? "quote file.cs" : "quote\"file.cs", "before\n", "after\n"), new("unicode-ą.cs", null, "new without newline"),
             new("deleted.cs", "delete\r\n", null)];
        var root = Path.Combine(Path.GetTempPath(), "xamlg-agent-patch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            foreach (var file in files.Where(file => file.Before != null))
                await File.WriteAllTextAsync(Path.Combine(root, file.Path), file.Before, new UTF8Encoding(false), TestContext.Current.CancellationToken);
            var patch = Path.Combine(root, "review.patch");
            await File.WriteAllTextAsync(patch, AgentSourceReview.Patch(new(1, files)), new UTF8Encoding(false), TestContext.Current.CancellationToken);
            await GitAsync(root, "init", "--quiet");
            await GitAsync(root, "config", "core.autocrlf", "false");
            await GitAsync(root, "apply", "--check", "--whitespace=nowarn", patch);
            await GitAsync(root, "apply", "--whitespace=nowarn", patch);
            foreach (var file in files)
                Assert.Equal(file.After, File.Exists(Path.Combine(root, file.Path)) ? await File.ReadAllTextAsync(Path.Combine(root, file.Path), TestContext.Current.CancellationToken) : null);
            await GitAsync(root, "apply", "--reverse", "--whitespace=nowarn", patch);
            foreach (var file in files)
                Assert.Equal(file.Before, File.Exists(Path.Combine(root, file.Path)) ? await File.ReadAllTextAsync(Path.Combine(root, file.Path), TestContext.Current.CancellationToken) : null);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static async Task GitAsync(string root, params string[] arguments)
    {
        var info = new ProcessStartInfo("git") { WorkingDirectory = root, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!;
        var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', arguments)} failed: {await error}{await output}");
    }
}
