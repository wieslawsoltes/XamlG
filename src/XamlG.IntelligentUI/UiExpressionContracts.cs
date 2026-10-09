using System.Text.Json;

namespace XamlG.IntelligentUI;

/// <summary>A reusable expression boundary. Implementations beyond the default pure interpreter
/// are executable application code and must be explicitly selected by the embedding host.</summary>
public interface IUiExpression
{
    string Source { get; }
    JsonElement Evaluate(JsonElement state, JsonElement data, JsonElement? item = null);
}
public interface IUiExpressionCompiler
{
    string Language { get; }
    IUiExpression Compile(string source, UiLimits limits);
}
public sealed partial class UiExpression : IUiExpression { }
