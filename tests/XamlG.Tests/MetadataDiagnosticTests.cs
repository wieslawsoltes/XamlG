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
}
