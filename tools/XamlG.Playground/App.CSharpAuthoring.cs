using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using XamlG.Automation;
using XamlG.Playground.Components;
using XamlG.Tooling;
using XamlG.Tooling.Refactoring;

namespace XamlG.Playground;

public partial class App
{
    private CSharpLanguageService CSharpLanguage(CancellationToken token = default)
    {
        var result = AnalyzeAutomation(token);
        return new(result.Compilation, result.SourcePaths);
    }

    private void AddCSharpAutomation()
    {
        AddAutomation<CSharpOperationsArguments>("csharp_operations", "Inspect real Roslyn IOperation trees in source or generated C#. UTF-16 (offset=0,length=0) selects all executable roots; other ranges select an enclosing operation or declaration. Flat IDs, types, constants, calls, conversions and truncation are result-local.", AutomationScope.Compiler, AutomationEffect.Read,
            (args, context) => new { revision = SourceRevision, operations = CSharpLanguage(context.CancellationToken).GetOperations(args.Path, args.Offset, args.Length, args.MaxDepth, args.MaxNodes, context.CancellationToken) });
        AddAutomation<CSharpControlFlowArguments>("csharp_control_flow", "Inspect Roslyn's actual control-flow graph for the body containing a UTF-16 offset, including nested local functions/lambdas. Returns bounded blocks, reachability, branches, regions, captures and lowered operations; does not execute code. IDs belong to this result.", AutomationScope.Compiler, AutomationEffect.Read,
            (args, context) => new { revision = SourceRevision, controlFlow = CSharpLanguage(context.CancellationToken).GetControlFlow(args.Path, args.Offset, args.MaxBlocks, args.MaxNodes, args.MaxDepth, args.MaxRegions, args.MaxSymbols, context.CancellationToken) });
        AddAutomation<CSharpDataFlowArguments>("csharp_data_flow", "Analyze Roslyn data flow for a C# expression, statement, contiguous statement range or body in source or generated code. Returns the actual analyzed region, success, bounded symbol tables and read/write/assignment/capture/flow sets.", AutomationScope.Compiler, AutomationEffect.Read,
            (args, context) => new { revision = SourceRevision, dataFlow = CSharpLanguage(context.CancellationToken).GetDataFlow(args.Path, args.Offset, args.Length, args.MaxSymbols, context.CancellationToken) });
        AddAutomation<PositionArguments>("csharp_hover", "Read C# symbol, type, constant, documentation and exact source/generated declarations at a UTF-16 offset.", AutomationScope.Compiler, AutomationEffect.Read,
            (args, context) => new { revision = SourceRevision, hover = CSharpLanguage(context.CancellationToken).GetSymbol(args.Path, args.Offset, context.CancellationToken) });
        AddAutomation<CSharpCompleteArguments>("csharp_complete", "Complete accessible C# symbols in lexical scope or on a receiver, with replacement span and explicit truncation.", AutomationScope.Compiler, AutomationEffect.Read,
            (args, context) => new { revision = SourceRevision, completions = CSharpLanguage(context.CancellationToken).GetCompletions(args.Path, args.Offset, args.MaxResults, context.CancellationToken) });
        AddAutomation<PositionArguments>("csharp_definitions", "Find exact C# declaration locations, including generated partial members.", AutomationScope.Compiler, AutomationEffect.Read,
            (args, context) => new { revision = SourceRevision, definitions = CSharpLanguage(context.CancellationToken).GetDefinitions(args.Path, args.Offset, context.CancellationToken) });
        AddAutomation<CSharpReferenceArguments>("csharp_references", "Find symbol-based C# references across source and generated files; excludes same-spelled locals, comments and strings.", AutomationScope.Compiler, AutomationEffect.Read,
            (args, context) => new { revision = SourceRevision, references = CSharpLanguage(context.CancellationToken).GetReferences(args.Path, args.Offset, args.IncludeDeclaration, args.MaxResults, context.CancellationToken) });
        AddAutomation<PositionArguments>("csharp_signatures", "Inspect candidate C# method/constructor overloads, resolved overload and current argument index.", AutomationScope.Compiler, AutomationEffect.Read,
            (args, context) => new { revision = SourceRevision, signatures = CSharpLanguage(context.CancellationToken).GetSignatures(args.Path, args.Offset, context.CancellationToken) });
        AddAutomation<FormatArguments>("csharp_format", "Normalize editable C# syntax whitespace using Roslyn in one revision-checked project undo step. Generated source is read-only.", AutomationScope.Source, AutomationEffect.Edit,
            (args, context) =>
            {
                CheckSourceRevision(args.ExpectedRevision);
                var edits = CSharpLanguage(context.CancellationToken).Format(args.Path, cancellationToken: context.CancellationToken);
                ApplyCSharpSource(args.Path, args.ExpectedRevision, edits, context.Caller + ": format C#");
                return new { revision = SourceRevision };
            });
        AddAutomation<PositionArguments>("csharp_actions", "List available semantic C# source actions at the selection with their exact edits; does not apply them.", AutomationScope.Source, AutomationEffect.Read,
            (args, context) => new { revision = SourceRevision, actions = CSharpLanguage(context.CancellationToken).GetActions(args.Path, args.Offset, context.CancellationToken) });
        AddAutomation<CSharpActionArguments>("csharp_action_apply", "Recompute and apply a selected C# source action at the current revision in one undo step.", AutomationScope.Source, AutomationEffect.Edit,
            (args, context) =>
            {
                CheckSourceRevision(args.ExpectedRevision);
                var actions = CSharpLanguage(context.CancellationToken).GetActions(args.Path, args.Offset, context.CancellationToken);
                var action = actions.SingleOrDefault(a => a.Title == args.Title) ?? throw new ArgumentException("That action is no longer available at this source position.");
                ApplyCSharpSource(args.Path, args.ExpectedRevision, action.Changes, context.Caller + ": " + action.Title);
                return new { revision = SourceRevision };
            });
        AddAutomation<RenameArguments>("csharp_rename_preview", "Plan a C# symbol rename with exact edits. Generated XAML fields route to the owning XAML declaration; other generated dependencies or inheritance contracts are rejected.", AutomationScope.Source, AutomationEffect.Read,
            (args, context) => { CheckSourceRevision(args.ExpectedRevision); return new { revision = SourceRevision, plan = PlanCSharpRename(args.Path, args.Offset, args.Name, context.CancellationToken) }; });
        AddAutomation<RenameArguments>("csharp_rename", "Apply a checked C# rename as one project undo step. Checks binding preservation and handles XAML-generated field names through the XAML rename service.", AutomationScope.Source, AutomationEffect.Edit,
            (args, context) =>
            {
                CheckSourceRevision(args.ExpectedRevision);
                var plan = PlanCSharpRename(args.Path, args.Offset, args.Name, context.CancellationToken);
                RestoreWorkspace(_workspaceEdits.Apply(args.ExpectedRevision, plan.Documents, context.Caller + ": rename C#", candidate => ValidateWorkspace(candidate.Documents)));
                return new { revision = SourceRevision, plan.OldName, plan.NewName };
            });
    }

    private void ApplyCSharpSource(string path, long revision, ImmutableArray<XamlG.Syntax.XamlTextChange> changes, string description) =>
        RestoreWorkspace(_workspaceEdits.Apply(revision, [new(path, Source(path), null, changes)], description, candidate => ValidateWorkspace(candidate.Documents)));

    // Interactive editor queries use the user's local editing authority. They share the source
    // gate with automation and reject a response if the requesting Monaco snapshot was replaced.
    private async Task<JsonElement> EditorLanguageQueryAsync(CSharpEditorQuery request)
    {
        if (!_ready || _busy) return AutomationJson.Element<object?>(null);
        await _automationGate.WaitAsync();
        try
        {
            if (_busy) return AutomationJson.Element<object?>(null);
            await CaptureEditorsAsync();
            var service = CSharpLanguage();
            if (service.Tree(request.Path).GetText().ToString() != request.Text) return AutomationJson.Element<object?>(null);
            object? result = request.Kind switch
            {
                "completion" => service.GetCompletions(request.Path, request.Offset),
                "hover" => service.GetSymbol(request.Path, request.Offset),
                "definition" => service.GetDefinitions(request.Path, request.Offset),
                "references" => service.GetReferences(request.Path, request.Offset),
                "signature" => service.GetSignatures(request.Path, request.Offset),
                "document" => new { text = service.Tree(request.TargetPath ?? throw new ArgumentException("A target document is required.")).GetText().ToString() },
                _ => throw new ArgumentException("Unknown C# editor query.")
            };
            return AutomationJson.Element(result);
        }
        catch (ArgumentException) { return AutomationJson.Element<object?>(null); }
        catch (KeyNotFoundException) { return AutomationJson.Element<object?>(null); }
        finally { _automationGate.Release(); }
    }

    private async Task CSharpAuthoringCommandAsync(EditorCommandRequest request)
    {
        _authoringRequest = request; _authoringRevision = SourceRevision;
        var service = CSharpLanguage();
        switch (request.Command)
        {
            case "rename":
                var symbol = service.ResolveSymbol(request.Path, request.Start) ?? throw new InvalidOperationException("Select a resolved C# identifier.");
                _renameName = _renameOriginal = symbol is IMethodSymbol { MethodKind: MethodKind.Constructor or MethodKind.Destructor } constructor ? constructor.ContainingType.Name : symbol.Name;
                _renameVisible = true; break;
            case "format":
                await ApplyAuthoringEditsAsync([new(request.Path, request.Text, null, service.Format(request.Path))], "Format C#"); break;
            case "actions":
                _codeActions = service.GetActions(request.Path, request.Start); _actionsVisible = true; break;
            default: throw new ArgumentException("Unknown C# authoring command.");
        }
    }

    private XamlRenamePlan PlanCSharpRename(string path, int offset, string name, CancellationToken token = default)
    {
        var service = CSharpLanguage(token); var compilation = _result!;
        if (service.ResolveSymbol(path, offset, token) is IFieldSymbol field)
        {
            foreach (var document in compilation.Project!.Documents)
            {
                if (document.Document.ClassSymbol?.ToDisplayString() != field.ContainingType.ToDisplayString()) continue;
                var analysis = new XamlAnalysis(document.Input.Syntax, document.Document, document.Output);
                var index = XamlNameReferenceIndex.Create(analysis, compilation.AuthoringCompiler!, token);
                var declaration = index.Occurrences.FirstOrDefault(o => o.IsDeclaration && o.Name == field.Name && o.NameScopeId == document.Document.Root?.NameScopeId);
                if (declaration != null)
                    return new XamlRenameService(compilation.AuthoringCompiler!).Rename(analysis, declaration.Span.Start, name,
                        compilation.Project.Documents.Select(d => new XamlAnalysis(d.Input.Syntax, d.Document, d.Output)), token);
            }
        }
        return new CSharpRenameService(compilation.Compilation, compilation.SourcePaths).Rename(path, offset, name, token);
    }

    public sealed record CSharpCompleteArguments(string Path, int Offset, int MaxResults = 200);
    public sealed record CSharpOperationsArguments(string Path, int Offset = 0, int Length = 0, int MaxDepth = 12, int MaxNodes = 2000);
    public sealed record CSharpControlFlowArguments(string Path, int Offset, int MaxBlocks = 500, int MaxNodes = 2000, int MaxDepth = 12, int MaxRegions = 1000, int MaxSymbols = 1000);
    public sealed record CSharpDataFlowArguments(string Path, int Offset, int Length = 0, int MaxSymbols = 1000);
    public sealed record CSharpReferenceArguments(string Path, int Offset, bool IncludeDeclaration = true, int MaxResults = 1000);
    public sealed record CSharpActionArguments(string Path, int Offset, string Title, long ExpectedRevision);
}
