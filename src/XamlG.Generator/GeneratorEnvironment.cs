using Microsoft.CodeAnalysis.CSharp;
using XamlG.Compiler;
using XamlG.CSharp.Resources;
using XamlG.Frameworks;
using XamlG.Roslyn;

namespace XamlG.Generator;

internal sealed class GeneratorEnvironment
{
    public GeneratorEnvironment(CSharpCompilation compilation, GeneratorOptions options)
    {
        Compilation = compilation; Options = options;
        try
        {
            Profile = KnownFrameworkProfiles.Select(compilation, options.Framework, options.CompileBindingsByDefault, options.CreateSourceInfo);
            Types = new(compilation, Profile.TypeSystem);
        }
        catch (ArgumentException error)
        {
            ConfigurationError = error.Message;
            Profile = XamlFrameworkProfile.Portable; Types = new(compilation);
        }
    }
    public XamlProjectCompiler ProjectCompiler { get; } = new();
    public CSharpCompilation Compilation { get; }
    public GeneratorOptions Options { get; }
    public XamlFrameworkProfile Profile { get; }
    public RoslynTypeSystem Types { get; }
    public string? ConfigurationError { get; }
}
