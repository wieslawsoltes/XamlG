using XamlG.Tooling;
using XamlG.Workspaces.Watching;

namespace XamlG.Lsp;

internal sealed record ServerCompilationState(XamlCompilationSession Compiler, XamlWatchInputs Inputs);
