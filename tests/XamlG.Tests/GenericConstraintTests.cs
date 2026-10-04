using XamlG.Compiler;
using XamlG.Syntax;
using Xunit;

namespace XamlG.Tests;

public sealed class GenericConstraintTests
{
    private const string Model = """
        using System;
        using System.Collections.Generic;
        using System.Diagnostics.CodeAnalysis;
        namespace Model {
          public class Root<T> where T : IDisposable, new() { }
          public class Good : IDisposable { public void Dispose() {} }
          public class WrongInterface { }
          public class PrivateConstructor : IDisposable { private PrivateConstructor() {} public void Dispose() {} }
          public abstract class Abstract : IDisposable { public abstract void Dispose(); }
          public class HasRequired : IDisposable { public required string Name {get;set;} public void Dispose() {} }
          public class SetsRequired : IDisposable { public required string Name {get;set;} [SetsRequiredMembers] public SetsRequired() { Name="set"; } public void Dispose() {} }
          public class Dependent<T, U> where T : IEnumerable<U> { }
          public class Sequence : List<string> { }
          public class ConversionOnly { public static implicit operator Sequence(ConversionOnly source) => new(); }
          public class BaseConstraint<T> where T : Sequence { }
          public class Related<T, U> where T : U { }
          public class Value<T> where T : struct { }
          public class Comparable<T> where T : IComparable { }
        }
        """;
    private static BoundDocument Bind(string root, string arguments) => new XamlCompiler().Bind(XamlSyntaxTree.Parse(
        "<" + root + " xmlns='clr-namespace:Model' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' x:TypeArguments='" + arguments + "'/>", "Constraints.xaml"), CompilationFactory.Create(Model));
    [Theory]
    [InlineData("WrongInterface")]
    [InlineData("PrivateConstructor")]
    [InlineData("Abstract")]
    [InlineData("HasRequired")]
    public void InterfaceAndConstructorConstraintsDiagnoseInXaml(string argument) =>
        Assert.Contains(Bind("Root", argument).Diagnostics, d => d.Code == "XG1025");
    [Theory]
    [InlineData("Good")]
    [InlineData("SetsRequired")]
    public void PublicConstructorsAndSatisfiedRequiredMembersAreAccepted(string argument)
    {
        var document = Bind("Root", argument);
        Assert.True(document.Success, string.Join("\n", document.Diagnostics));
    }
    [Fact]
    public void DependentGenericInterfaceConstraintsAreSubstituted()
    {
        Assert.True(Bind("Dependent", "Sequence,x:String").Success);
        Assert.Contains(Bind("Dependent", "Sequence,x:Int32").Diagnostics, d => d.Code == "XG1025");
    }
    [Fact]
    public void NumericAndUserDefinedConversionsDoNotSatisfyConstraints()
    {
        Assert.Contains(Bind("BaseConstraint", "ConversionOnly").Diagnostics, d => d.Code == "XG1025");
        Assert.Contains(Bind("Related", "x:Int32,x:Int64").Diagnostics, d => d.Code == "XG1025");
        Assert.True(Bind("Related", "Good,x:Object").Success);
    }
    [Fact]
    public void NullableValueArgumentsDoNotSatisfyStructOrUnderlyingInterfaceConstraints()
    {
        Assert.Contains(Bind("Value", "x:Int32?").Diagnostics, d => d.Code == "XG1025");
        Assert.Contains(Bind("Comparable", "x:Int32?").Diagnostics, d => d.Code == "XG1025");
        Assert.True(Bind("Comparable", "x:Int32").Success);
    }
}
