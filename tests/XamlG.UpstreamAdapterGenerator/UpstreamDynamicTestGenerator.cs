using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace XamlG.UpstreamAdapterGenerator;

/// <summary>Retains upstream runtime assertions while replacing their IL-specific configuration harness.
/// The single IL-helper-metadata-name assertion is not a source-backend contract and is explicitly excluded.</summary>
[Generator]
public sealed class UpstreamDynamicTestGenerator : IIncrementalGenerator
{
    private const string FixtureName = "DynamicSettersTests";
    private const string MetadataOnlyTest = "Dynamic_Setter_For_Public_Property_Should_Be_Shared";

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var source = context.AdditionalTextsProvider.Where(file => file.Path.EndsWith("/DynamicSettersTests.cs", StringComparison.Ordinal) || file.Path.EndsWith("\\DynamicSettersTests.cs", StringComparison.Ordinal))
            .Select((file, cancellation) => file.GetText(cancellation)?.ToString());
        context.RegisterSourceOutput(source, (production, text) =>
        {
            if (text == null) return;
            var syntax = CSharpSyntaxTree.ParseText(text, cancellationToken: production.CancellationToken).GetCompilationUnitRoot(production.CancellationToken);
            var fixture = syntax.DescendantNodes().OfType<ClassDeclarationSyntax>().Single(node => node.Identifier.ValueText == FixtureName);
            var tests = fixture.Members.OfType<MethodDeclarationSyntax>()
                .Where(method => method.AttributeLists.Count > 0 && method.Identifier.ValueText != MetadataOnlyTest)
                .Cast<MemberDeclarationSyntax>().ToList();
            tests.Add(SyntaxFactory.ParseMemberDeclaration("protected override XamlG.Compiler.XamlFrameworkProfile ConfigureProfile(XamlG.Compiler.XamlFrameworkProfile profile) => profile with { PropertyBindingRules = System.Collections.Immutable.ImmutableArray.Create<XamlG.Compiler.IXamlPropertyBindingRule>(new XamlG.Compiler.XamlDynamicPropertyRule(new UpstreamDynamicSetterProvider())) };")!);
            tests.Add(SyntaxFactory.ParseMemberDeclaration("private object CompileAndRunWithSpecialTransformer(string xaml) => CompileAndRun(xaml);")!);
            syntax = syntax.ReplaceNode(fixture, fixture.WithMembers(SyntaxFactory.List(tests)));
            production.AddSource("UpstreamDynamicTests.g.cs", SourceText.From("// Pinned XamlX tests: only the IL-specific setup is adapted. MIT; see THIRD-PARTY-NOTICES.md.\n#nullable enable\n" + syntax.NormalizeWhitespace().ToFullString(), Encoding.UTF8));
        });
    }
}
