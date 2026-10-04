using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using XamlG.Frameworks.Avalonia;
using XamlG.Syntax;
using XamlG.Tooling;
using XamlG.Tooling.Refactoring;
using Xunit;

namespace XamlG.Avalonia.Tests;

public sealed class NameRefactoringTests
{
    [Theory]
    [InlineData("<TextBlock Text='{Binding #target.Text}'/>")]
    [InlineData("<TextBlock Text='{Binding ElementName=target, Path=Text}'/>")]
    [InlineData("<TextBlock Text='{CompiledBinding Path=#target.Text}'/>")]
    [InlineData("<TextBlock><TextBlock.Text><Binding ElementName='target' Path='Text'/></TextBlock.Text></TextBlock>")]
    [InlineData("<TextBlock Text=\"{CompiledBinding ElementName=t&#97;rget, Path=Text}\"/>")]
    public void BindingSourcesAndNamedPathsParticipateInRename(string reference)
    {
        var token = TestContext.Current.CancellationToken;
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator).Select(p => MetadataReference.CreateFromFile(p));
        var compilation = CSharpCompilation.Create("AvaloniaRename", references: references,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var compiler = new XamlCompilationSession(compilation, AvaloniaFrameworkProfile.Create());
        var syntax = XamlSyntaxTree.Parse("<StackPanel " + ResourceProjectFixture.Namespace + "><TextBlock x:Name='target' Text='source'/>" + reference + "</StackPanel>", "View.axaml", token);
        var analysis = compiler.Analyze(syntax, token);
        Assert.True(analysis.Output.Success, string.Join("\n", analysis.Output.Diagnostics));
        var names = XamlNameReferenceIndex.Create(analysis, compiler, token);
        Assert.Equal(2, names.Occurrences.Length);
        var plan = new XamlRenameService(compiler).Rename(analysis, syntax.Text.IndexOf("'target'", StringComparison.Ordinal) + 1, "renamed", cancellationToken: token);
        var updated = syntax.WithChanges(Assert.Single(plan.Documents).Changes, syntax.Version, token);
        Assert.DoesNotContain("target", updated.Text); Assert.DoesNotContain("t&#97;rget", updated.Text);
        var changed = compiler.Analyze(updated, token);
        Assert.True(changed.Output.Success, string.Join("\n", changed.Output.Diagnostics));
        var built = compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(changed.Output.Source, new CSharpParseOptions(LanguageVersion.Preview), cancellationToken: token));
        using var output = new MemoryStream();
        var result = built.Emit(output, cancellationToken: token);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
    }
}
