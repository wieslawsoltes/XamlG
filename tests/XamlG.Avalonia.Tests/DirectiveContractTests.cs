using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using XamlG.Compiler;
using XamlG.CSharp;
using XamlG.CSharp.Integration;
using XamlG.CSharp.Resources;
using XamlG.Frameworks.Avalonia;
using XamlG.Syntax;
using XamlG.Tooling;
using Xunit;

namespace XamlG.Avalonia.Tests;

public sealed class DirectiveContractTests
{
    private const string Ns = ResourceProjectFixture.Namespace + " xmlns:t='clr-namespace:XamlG.Avalonia.Tests;assembly=XamlG.Avalonia.Tests'";

    public static IEnumerable<object[]> IgnoredDirectives()
    {
        foreach (var directive in new[] { "Precompile", "ClassModifier", "FieldModifier", "Class" })
            foreach (var nested in new[] { false, true })
                foreach (var value in new[] { "true", "false", "invalid", "", " Public ", "{x:Null}", "{t:DirectiveValue}" })
                    if (directive != "Class" || nested) yield return new object[] { directive, nested, value };
    }

    [AvaloniaTheory]
    [MemberData(nameof(IgnoredDirectives))]
    public void RuntimeCompilationDiscardsBuildDirectives(string directive, bool nested, string value)
    {
        var assignment = " x:" + directive + "='" + value + "'";
        var xaml = "<StackPanel " + Ns + (nested ? "" : assignment) + "><Border Name='target' Width='12'" + (nested ? assignment : "") + "/></StackPanel>";
        var baseline = AvaloniaUpstreamCompilation.Compile(xaml);
        Assert.Null(baseline.Error);
        foreach (var root in new[] { Assert.IsType<StackPanel>(baseline.Root), Assert.IsType<StackPanel>(AvaloniaCompilation.Build(xaml)) })
        {
            var child = Assert.IsType<Border>(Assert.Single(root.Children));
            Assert.Equal("target", child.Name);
            Assert.Equal(12, child.Width);
        }
    }

    [AvaloniaTheory]
    [InlineData("Precompile")]
    [InlineData("ClassModifier")]
    [InlineData("FieldModifier")]
    public void IgnoredDirectivesStillRequireValidMarkupSyntax(string directive)
    {
        var xaml = "<Border " + Ns + " x:" + directive + "='{'/>";
        Assert.NotNull(AvaloniaUpstreamCompilation.Compile(xaml).Error);
        var compilation = new ResourceProjectFixture(Array.Empty<(string, string)>()).Compilation;
        Assert.False(new XamlCompiler().Bind(XamlSyntaxTree.Parse(xaml), compilation, AvaloniaFrameworkProfile.Create()).Success);
    }

    [AvaloniaTheory]
    [InlineData("{missing:Value}", true)]
    [InlineData("{t:DirectiveValue Value={ }}", false)]
    [InlineData("{t:DirectiveValue missing:Value=text}", true)]
    public void DiscardedDirectiveMarkupValidatesSyntaxWithoutResolvingNames(string value, bool valid)
    {
        var xaml = "<Border " + Ns + " x:FieldModifier='" + value + "'/>";
        Assert.Equal(valid, AvaloniaUpstreamCompilation.Compile(xaml).Error == null);
        var compilation = new ResourceProjectFixture(Array.Empty<(string, string)>()).Compilation;
        Assert.Equal(valid, new XamlCompiler().Bind(XamlSyntaxTree.Parse(xaml), compilation, AvaloniaFrameworkProfile.Create()).Success);
    }

    [Theory]
    [InlineData("Tag='{'", "")]
    [InlineData("x:ClassModifier='{'", "")]
    [InlineData("", "<missing:Child/>")]
    [InlineData("x:TypeArguments='t:Value('", "")]
    public void OptOutRetainsSyntaxValidationBeforeTypeResolution(string attributes, string children)
    {
        var xaml = "<Missing " + Ns + " x:Precompile='False' " + attributes + ">" + children + "</Missing>";
        var result = Assert.Single(new ResourceProjectFixture(new[] { ("Skipped.axaml", xaml) }).Result.Documents);
        Assert.True(result.Document.IsSkipped);
        Assert.False(result.Output.Success);
        Assert.NotEmpty(result.Output.Diagnostics);
        Assert.DoesNotContain(result.Output.Diagnostics, diagnostic => diagnostic.Code == "XG1003");
    }

    [Theory]
    [InlineData("false")]
    [InlineData("False")]
    [InlineData("  fAlSe  ")]
    [InlineData("&#x9;FALSE&#xA;")]
    public void PrecompileFalseSkipsTypeResolutionFactoriesAndExports(string value)
    {
        var xaml = "<Missing " + Ns + " x:Precompile='" + value + "' x:Class='Missing.Class' x:ClassModifier='invalid' Missing='value'><MissingChild/></Missing>";
        var fixture = new ResourceProjectFixture(new[] { ("Skipped.axaml", xaml), ("Kept.axaml", "<Border " + Ns + " Width='12'/>") });
        Assert.True(fixture.Result.Success, string.Join("\n", fixture.Result.Documents.SelectMany(document => document.Output.Diagnostics)));
        var skipped = fixture.Result.Documents.Single(document => document.Input.LogicalPath == "Skipped.axaml");
        Assert.True(skipped.Document.IsSkipped);
        Assert.True(skipped.Output.IsSkipped);
        Assert.Null(skipped.Document.Root);
        Assert.Empty(skipped.Output.Source);
        Assert.Null(skipped.Output.BuildMethodName);
        Assert.Single(fixture.Result.Resources.Resources);
        Assert.Single(XamlCSharpCompilation.Sources(fixture.Result));
        Assert.Equal(12, Assert.IsType<Border>(fixture.Build("Kept.axaml")).Width);
    }

    [Theory]
    [InlineData("true")]
    [InlineData("True")]
    [InlineData("  tRuE  ")]
    [InlineData("&#x9;TRUE&#xA;")]
    public void PrecompileTrueIncludesTheDocument(string value)
    {
        var fixture = new ResourceProjectFixture(new[] { ("Kept.axaml", "<Border " + Ns + " x:Precompile='" + value + "' Width='12'/>") });
        Assert.False(Assert.Single(fixture.Result.Documents).Document.IsSkipped);
        Assert.Equal(12, Assert.IsType<Border>(fixture.Build("Kept.axaml")).Width);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("yes")]
    [InlineData("no")]
    [InlineData("{x:Null}")]
    [InlineData("{t:DirectiveValue}")]
    public void InvalidRootPrecompileValuesProduceLocatedBuildDiagnostics(string value)
    {
        var xaml = "<Border " + Ns + " x:Precompile='" + value + "'/>";
        var fixture = new ResourceProjectFixture(new[] { ("Invalid.axaml", xaml) });
        var result = Assert.Single(fixture.Result.Documents);
        Assert.False(result.Output.Success);
        var diagnostic = Assert.Single(result.Output.Diagnostics.Where(diagnostic => diagnostic.Code == "XG1018"));
        Assert.Equal(value, xaml.Substring(diagnostic.Span.Start, diagnostic.Span.Length));
        Assert.Empty(result.Output.Source);
    }

    [Fact]
    public void OptOutDoesNotSuppressMalformedXmlDiagnostics()
    {
        var fixture = new ResourceProjectFixture(new[] { ("Invalid.axaml", "<Border " + Ns + " x:Precompile='False'><Bad></Border>") });
        Assert.False(fixture.Result.Success);
        Assert.Contains(Assert.Single(fixture.Result.Documents).Output.Diagnostics, diagnostic => diagnostic.Code.StartsWith("XG00", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public void NestedBuildDirectivesDoNotSkipTheDocumentOrResolveNestedClasses()
    {
        var xaml = "<StackPanel " + Ns + "><Border Width='12' x:Precompile='invalid' x:Class='Missing.Class' x:ClassModifier='invalid'/></StackPanel>";
        var root = Assert.IsType<StackPanel>(new ResourceProjectFixture(new[] { ("Kept.axaml", xaml) }).Build("Kept.axaml"));
        Assert.Equal(12, Assert.Single(root.Children).Width);
    }

    [Theory]
    [InlineData("public", "public")]
    [InlineData("PUBLIC", "public")]
    [InlineData("private", "private")]
    [InlineData("Private", "private")]
    [InlineData("protected", "protected")]
    [InlineData("internal", "internal")]
    [InlineData("NotPublic", "internal")]
    [InlineData("nOtPuBlIc", "internal")]
    [InlineData(" Public ", "internal")]
    [InlineData("protected internal", "internal")]
    [InlineData("private protected", "internal")]
    [InlineData("invalid", "internal")]
    [InlineData("", "internal")]
    [InlineData("{x:Null}", "internal")]
    public void NamedFieldModifiersFollowTheNameGeneratorGrammar(string value, string expected)
    {
        var xaml = "<StackPanel " + Ns + " x:Class='Demo.DirectiveView'><Border Name='target' x:FieldModifier='" + value + "'/></StackPanel>";
        var fixture = new ResourceProjectFixture(new[] { ("View.axaml", xaml) }, sourceCode: "namespace Demo; public partial class DirectiveView : Avalonia.Controls.StackPanel { }");
        var type = ResourceProjectFixture.Load(fixture.Emit()).GetType("Demo.DirectiveView")!;
        var field = type.GetField("target", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!;
        Assert.Equal(expected == "public", field.IsPublic);
        Assert.Equal(expected == "private", field.IsPrivate);
        Assert.Equal(expected == "protected", field.IsFamily);
        Assert.Equal(expected == "internal", field.IsAssembly);
    }

    [Theory]
    [InlineData("public", true)]
    [InlineData("Public", true)]
    [InlineData(" PUBLIC ", true)]
    [InlineData("internal", false)]
    [InlineData("Internal", false)]
    [InlineData("NotPublic", false)]
    [InlineData(" notpublic ", false)]
    public void ClassModifiersControlFactoryVisibilityAndExports(string value, bool isPublic)
    {
        var fixture = new ResourceProjectFixture(new[] { ("View.axaml", "<Border " + Ns + " x:ClassModifier='" + value + "'/>") });
        var result = Assert.Single(fixture.Result.Documents);
        var assembly = ResourceProjectFixture.Load(fixture.Emit());
        Assert.Equal(isPublic, assembly.GetType(result.Output.FactoryMetadataName)!.IsPublic);
        Assert.Equal(isPublic, result.Output.Source.Contains("XamlCompiledResourceAttribute", StringComparison.Ordinal));
        Assert.Single(fixture.Result.Resources.Resources);
    }

    [Theory]
    [InlineData("public", "Public", true)]
    [InlineData("public", "NotPublic", false)]
    [InlineData("internal", "Public", false)]
    [InlineData("internal", "NotPublic", true)]
    [InlineData("internal", " Internal ", true)]
    [InlineData("public", "invalid", false)]
    [InlineData("public", "", false)]
    [InlineData("public", "{x:Null}", false)]
    public void ClassModifiersValidateTheActualCodeBehindAccessibility(string accessibility, string modifier, bool valid)
    {
        var xaml = "<Border " + Ns + " x:Class='Demo.DirectiveView' x:ClassModifier='" + modifier + "'/>";
        var fixture = new ResourceProjectFixture(new[] { ("View.axaml", xaml) }, sourceCode: "namespace Demo; " + accessibility + " partial class DirectiveView : Avalonia.Controls.Border { }");
        Assert.Equal(valid, fixture.Result.Success);
        if (valid) fixture.Emit();
        else Assert.Contains(Assert.Single(fixture.Result.Documents).Output.Diagnostics, diagnostic => diagnostic.Code == "XG1018");
    }

    [Fact]
    public void SkippedCodeBehindDeclarationsDoNotConflictWithCompiledDocuments()
    {
        var root = "<Border " + Ns + " x:Class='Demo.DirectiveView'";
        var fixture = new ResourceProjectFixture(new[] { ("Skipped.axaml", root + " x:Precompile='False'/>"), ("Kept.axaml", root + "/>") },
            sourceCode: "namespace Demo; public partial class DirectiveView : Avalonia.Controls.Border { }");
        Assert.True(fixture.Result.Success);
        Assert.Single(fixture.Result.Documents.Where(document => !document.Document.IsSkipped));
        fixture.Emit();
    }

    [Fact]
    public void ChangingPrecompileInvalidatesResourceCatalogAndCachedBindings()
    {
        var fixture = new ResourceProjectFixture(Array.Empty<(string, string)>());
        var compiler = new XamlProjectCompiler();
        var profile = AvaloniaFrameworkProfile.Create();
        var enabled = new XamlProjectDocument(XamlSyntaxTree.Parse("<Border " + Ns + " x:Precompile='True'/>", "View.axaml", TestContext.Current.CancellationToken), "View.axaml");
        var disabled = enabled with { Syntax = XamlSyntaxTree.Parse("<Missing " + Ns + " x:Precompile='False'/>", "View.axaml", TestContext.Current.CancellationToken) };
        var first = compiler.Compile(new[] { enabled }, fixture.Compilation, profile, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Single(first.Resources.Resources);
        Assert.True(first.Success);
        var skipped = compiler.Compile(new[] { disabled }, fixture.Compilation, profile, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Empty(skipped.Resources.Resources);
        Assert.True(Assert.Single(skipped.Documents).Output.IsSkipped);
        Assert.Equal(1, compiler.Compile(new[] { disabled }, fixture.Compilation, profile, cancellationToken: TestContext.Current.CancellationToken).Statistics.ReusedBindings);
        var restored = compiler.Compile(new[] { enabled }, fixture.Compilation, profile, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Single(restored.Resources.Resources);
        Assert.True(restored.Success);
        Assert.False(Assert.Single(restored.Documents).Output.IsSkipped);
    }

    [Fact]
    public void WorkspaceAnalysisRetainsSkippedDocumentsAndAcceptsAnEnabledOverlay()
    {
        var fixture = new ResourceProjectFixture(Array.Empty<(string, string)>());
        var syntax = XamlSyntaxTree.Parse("<Missing " + Ns + " x:Precompile='False'/>", "View.axaml", TestContext.Current.CancellationToken);
        var session = new XamlCompilationSession(fixture.Compilation, AvaloniaFrameworkProfile.Create(),
            projectDocuments: new[] { new XamlProjectDocument(syntax, "View.axaml") });
        Assert.True(session.Analyze(syntax, TestContext.Current.CancellationToken).Output.IsSkipped);
        var overlay = XamlSyntaxTree.Parse("<Border " + Ns + " x:Precompile='True' Width='12'/>", syntax.Path, TestContext.Current.CancellationToken);
        var analysis = Assert.Single(session.AnalyzeWorkspace(new[] { overlay }, TestContext.Current.CancellationToken));
        Assert.False(analysis.Output.IsSkipped);
        Assert.True(analysis.Output.Success);
    }
}

public sealed class DirectiveValueExtension
{
    public object ProvideValue() => throw new InvalidOperationException("Ignored directive providers must not execute.");
}
