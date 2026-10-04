using XamlG.Compiler.Resources;
using XamlG.Syntax;
using Xunit;

namespace XamlG.Tooling.Tests;

public sealed class ResourceInspectionTests
{
    [Fact]
    public void InspectionExposesResourceIdentityAndResolvedRootType()
    {
        var type = ToolingFixture.Create().Types.Compilation.GetTypeByMetadataName("Model.View")!;
        var expression = new BoundResourceExpression(
            new("xamlg://application/Views/Reusable.xaml", type, "Views/Reusable.xaml", "XamlG.Generated", null), new TextSpan(12, 5));
        var inspected = XamlInspector.Expression(expression);
        Assert.Equal("xamlg://application/Views/Reusable.xaml (project factory)", inspected.Label);
        Assert.Equal("Model.View", inspected.TypeName);
        Assert.Equal(new TextSpan(12, 5), inspected.Span);
        Assert.Empty(inspected.Children);
    }
}
