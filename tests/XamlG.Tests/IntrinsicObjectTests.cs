using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using XamlG.Compiler;
using XamlG.Syntax;
using Xunit;

namespace XamlG.Tests;

public sealed class IntrinsicObjectTests
{
    private const string Model = """
        using System;
        using System.Collections.Generic;
        namespace Demo
        {
            public class View
            {
                public object Value { get; set; }
                public object Other { get; set; }
                public List<bool> Flags { get; } = new();
                public static string Text => "static";
            }
            public class Item { }
        }
        namespace Alternate { public class Item { } }
        """;
    private const string Prefix = "<View xmlns='clr-namespace:Demo' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>";

    [Theory]
    [InlineData("<x:Type><x:Type.TypeName>x:String</x:Type.TypeName></x:Type>")]
    [InlineData("<x:Type><x:Type.TypeName xmlns:sys='using:System'>sys:String</x:Type.TypeName></x:Type>")]
    public void ResolvesTypePropertyElementsInTheirOwnScope(string value)
    {
        using var code = CompiledXaml.Create(Prefix + "<View.Value>" + value + "</View.Value></View>", Model);
        var root = code.Build();
        Assert.Equal(typeof(string), root.GetType().GetProperty("Value")!.GetValue(root));
    }

    [Fact]
    public void ExplicitGenericArgumentsKeepTheAttributeNamespaceScope()
    {
        using var code = CompiledXaml.Create(Prefix + """
            <View.Value>
              <x:Type xmlns:local="clr-namespace:Demo" x:TypeArguments="local:Item">
                <x:Type.TypeName xmlns:local="using:System.Collections.Generic">local:List</x:Type.TypeName>
              </x:Type>
            </View.Value></View>
            """, Model);
        var root = code.Build();
        var type = Assert.IsAssignableFrom<Type>(root.GetType().GetProperty("Value")!.GetValue(root));
        Assert.Equal(typeof(List<>), type.GetGenericTypeDefinition());
        Assert.Equal("Demo.Item", Assert.Single(type.GenericTypeArguments).FullName);
    }

    [Theory]
    [InlineData("<x:Static><x:Static.Member>View.Text</x:Static.Member></x:Static>")]
    [InlineData("<x:Static Member='  View.Text  '/>")]
    public void ResolvesStaticPropertyElementsAndSurroundingWhitespace(string value)
    {
        using var code = CompiledXaml.Create(Prefix + "<View.Value>" + value + "</View.Value></View>", Model);
        var root = code.Build();
        Assert.Equal("static", root.GetType().GetProperty("Value")!.GetValue(root));
    }

    [Fact]
    public void ReferencePropertyElementsRetainForwardNameResolution()
    {
        using var code = CompiledXaml.Create(Prefix + """
            <View.Value><x:Reference><x:Reference.Name>later</x:Reference.Name></x:Reference></View.Value>
            <View.Other><Item x:Name="later"/></View.Other>
            </View>
            """, Model);
        var root = code.Build();
        Assert.Same(root.GetType().GetProperty("Other")!.GetValue(root), root.GetType().GetProperty("Value")!.GetValue(root));
    }

    [Fact]
    public void BooleanIntrinsicsParticipateInCollectionOverloadSelection()
    {
        using var code = CompiledXaml.Create(Prefix + "<View.Flags><x:True/><x:False/></View.Flags></View>", Model);
        var root = code.Build();
        Assert.Equal(new[] { true, false }, (IEnumerable<bool>)root.GetType().GetProperty("Flags")!.GetValue(root)!);
    }

    [Theory]
    [InlineData("<x:Type TypeName='x:String' Extra='ignored'/>")]
    [InlineData("<x:Type TypeName='x:String'><x:Type.TypeName>x:Int32</x:Type.TypeName></x:Type>")]
    [InlineData("<x:Type TypeName='x:String'><Item/></x:Type>")]
    [InlineData("<x:True Unknown='ignored'/>")]
    [InlineData("<x:Null><Item/></x:Null>")]
    public void MalformedIntrinsicObjectsDoNotSilentlyDropInput(string value)
    {
        var document = new XamlCompiler().Bind(XamlSyntaxTree.Parse(Prefix + "<View.Value>" + value + "</View.Value></View>"), CompilationFactory.Create(Model));
        Assert.False(document.Success);
        Assert.Contains(document.Diagnostics, diagnostic => diagnostic.Code == "XG1009");
    }

    [Theory]
    [InlineData("<View xmlns='clr-namespace:Demo' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' Value='{x:True}'/>")]
    [InlineData("<View xmlns='clr-namespace:Demo' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'><View.Value><x:True/></View.Value></View>")]
    public void FrameworkRulesCanOverrideIntrinsicForms(string xaml)
    {
        var profile = XamlFrameworkProfile.Portable with { MarkupBindingRules = ImmutableArray.Create<IXamlMarkupBindingRule>(new BooleanRule()) };
        using var code = CompiledXaml.Create(xaml, Model, profile);
        var root = code.Build();
        Assert.Equal(false, root.GetType().GetProperty("Value")!.GetValue(root));
    }

    private sealed class BooleanRule : IXamlMarkupBindingRule
    {
        public bool TryBind(BindingContext context, MarkupExtensionSyntax syntax, ITypeSymbol targetType, NamespaceScope scope, out BoundExpression? expression)
        {
            expression = new BoundConstantExpression(false, context.Types.Special(SpecialType.System_Boolean), syntax.Span);
            return scope.Expand(syntax.Name).LocalName == "True";
        }
    }
}
