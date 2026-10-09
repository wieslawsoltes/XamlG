using XamlG.Compiler;
using XamlG.Syntax;
using Xunit;

namespace XamlG.Tests;

public sealed class MetadataDiagnosticTests
{
    [Fact]
    public void ReportsOnlyRecognizedMetadataAndRetainsNamesSeverityAndLocations()
    {
        const string model = """
            namespace Metadata;
            public class ObsoleteAttribute : System.Attribute { }
            public class View
            {
                [System.Obsolete("Use Current", DiagnosticId = "OLD100")]
                public int Old { get; set; }
                [System.Obsolete("Removed", true)]
                public int Removed { get; set; }
                [System.Diagnostics.CodeAnalysis.Experimental("FUTURE100", Message = "Preview API")]
                public int Future { get; set; }
                [Obsolete]
                public int Current { get; set; }
            }
            """;
        const string xaml = "<View xmlns='clr-namespace:Metadata' Old='1' Removed='2' Future='3' Current='4'/>";
        var document = new XamlCompiler().Bind(XamlSyntaxTree.Parse(xaml, "Metadata.xaml"), CompilationFactory.Create(model));
        Assert.False(document.Success);
        Assert.Equal(3, document.Diagnostics.Length);
        var old = Assert.Single(document.Diagnostics, diagnostic => diagnostic.Code == "OLD100");
        Assert.Equal(XamlSeverity.Warning, old.Severity);
        Assert.Equal("'View.Old' is obsolete: Use Current", old.Message);
        var removed = Assert.Single(document.Diagnostics, diagnostic => diagnostic.Code == "XG2001");
        Assert.Equal(XamlSeverity.Error, removed.Severity);
        Assert.Contains("View.Removed", removed.Message);
        var future = Assert.Single(document.Diagnostics, diagnostic => diagnostic.Code == "FUTURE100");
        Assert.Equal(XamlSeverity.Warning, future.Severity);
        Assert.Contains("View.Future", future.Message);
        Assert.Contains("Preview API", future.Message);
        foreach (var diagnostic in document.Diagnostics)
            Assert.Contains(xaml.Substring(diagnostic.Span.Start, diagnostic.Span.Length), new[] { "Old", "Removed", "Future" });
    }

    [Fact]
    public void CachedDescriptionsKeepEachOccurrenceLocationAcrossConcurrentDocuments()
    {
        var compilation = CompilationFactory.Create("""
            namespace Metadata;
            public class View {
                public System.Collections.Generic.List<View> Children { get; } = new();
                [System.Obsolete("Use Current", DiagnosticId = "OLD100")]
                public int Old { get; set; }
            }
            """);
        Parallel.For(0, 16, index =>
        {
            var source = new string(' ', index) + "<View xmlns='clr-namespace:Metadata'><View.Children>" +
                string.Concat(Enumerable.Repeat("<View Old='1'/>", 20)) + "</View.Children></View>";
            var document = new XamlCompiler().Bind(XamlSyntaxTree.Parse(source, index + ".xaml"), compilation);
            Assert.True(document.Success);
            Assert.Equal(20, document.Diagnostics.Length);
            Assert.Equal(20, document.Diagnostics.Select(diagnostic => diagnostic.Span.Start).Distinct().Count());
            foreach (var diagnostic in document.Diagnostics)
            {
                Assert.Equal("OLD100", diagnostic.Code);
                Assert.Equal("'View.Old' is obsolete: Use Current", diagnostic.Message);
                Assert.Equal("Old", source.Substring(diagnostic.Span.Start, diagnostic.Span.Length));
            }
        });
    }

    [Fact]
    public void SameNamedSymbolsFromDifferentCompilationsDoNotShareMetadata()
    {
        const string model = "namespace Metadata; public class View { ATTRIBUTE public int Value { get; set; } }";
        var current = CompilationFactory.Create(model.Replace("ATTRIBUTE", "", StringComparison.Ordinal));
        var old = CompilationFactory.Create(model.Replace("ATTRIBUTE", "[System.Obsolete(\"Removed\", true)]", StringComparison.Ordinal));
        var preview = CompilationFactory.Create(model.Replace("ATTRIBUTE", "[System.Diagnostics.CodeAnalysis.Experimental(\"PREVIEW100\")]", StringComparison.Ordinal));
        var syntax = XamlSyntaxTree.Parse("<View xmlns='clr-namespace:Metadata' Value='1'/>");
        for (var iteration = 0; iteration < 3; iteration++)
        {
            Assert.Empty(new XamlCompiler().Bind(syntax, current).Diagnostics);
            var removed = Assert.Single(new XamlCompiler().Bind(syntax, old).Diagnostics);
            Assert.Equal("XG2001", removed.Code);
            Assert.Equal(XamlSeverity.Error, removed.Severity);
            var experimental = Assert.Single(new XamlCompiler().Bind(syntax, preview).Diagnostics);
            Assert.Equal("PREVIEW100", experimental.Code);
            Assert.Equal(XamlSeverity.Warning, experimental.Severity);
        }
    }
}
