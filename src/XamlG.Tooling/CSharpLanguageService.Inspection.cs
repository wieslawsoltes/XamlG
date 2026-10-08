using System.Collections.Immutable;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.FlowAnalysis;
using Microsoft.CodeAnalysis.Operations;
using Span = Microsoft.CodeAnalysis.Text.TextSpan;

namespace XamlG.Tooling;

public sealed partial class CSharpLanguageService
{
    /// <summary>At (0, 0), inspect executable roots throughout the file. Otherwise inspect the
    /// smallest enclosing operation, or the executable roots inside the selected declaration.</summary>
    public CSharpOperationResult GetOperations(string path, int offset = 0, int length = 0,
        int maximumDepth = 12, int maximumNodes = 2000, CancellationToken cancellationToken = default)
    {
        Bound(maximumNodes); DepthBound(maximumDepth);
        var tree = Tree(path); var selection = offset == 0 && length == 0 ? tree.GetRoot(cancellationToken)
            : InspectionSelection(tree, offset, length, cancellationToken);
        var model = _compilation.GetSemanticModel(tree);
        var collector = new OperationCollector(maximumDepth, maximumNodes, cancellationToken);
        var roots = ImmutableArray.CreateBuilder<int>();
        var wholeFile = offset == 0 && length == 0;
        var operation = wholeFile ? null : EnclosingOperation(model, selection, cancellationToken);
        if (operation != null) roots.Add(collector.Add(operation)!.Value);
        else
        {
            // Do not revisit descendants after Roslyn supplied an operation for their parent.
            // This also handles abstract declarations, initializers and top-level statements.
            var pending = new Stack<SyntaxNode>(); pending.Push(selection);
            while (pending.Count != 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var node = pending.Pop(); var candidate = model.GetOperation(node, cancellationToken);
                if (candidate != null)
                {
                    var id = collector.Add(candidate);
                    if (id == null) break;
                    roots.Add(id.Value);
                }
                else foreach (var child in node.ChildNodes().Reverse()) pending.Push(child);
            }
        }
        return new(path, wholeFile ? new(0, tree.Length) : new(offset, length), !_editable.Contains(path),
            roots.ToImmutable(), collector.Nodes.ToImmutableArray(), collector.Truncated);
    }

    /// <summary>Inspect the executable body containing an offset. Local functions and lambdas
    /// resolve through their enclosing graphs rather than presenting the outer body's graph.</summary>
    public CSharpControlFlowResult GetControlFlow(string path, int offset, int maximumBlocks = 500,
        int maximumNodes = 2000, int maximumDepth = 12, int maximumRegions = 1000,
        int maximumSymbols = 1000, CancellationToken cancellationToken = default)
    {
        Bound(maximumBlocks); Bound(maximumNodes); Bound(maximumRegions); Bound(maximumSymbols); DepthBound(maximumDepth);
        var tree = Tree(path); var selection = InspectionSelection(tree, offset, 0, cancellationToken);
        var model = _compilation.GetSemanticModel(tree);
        var operation = EnclosingOperation(model, selection, cancellationToken)
            ?? throw new ArgumentException("Select an executable C# body, initializer, statement or expression.", nameof(offset));
        var nested = new Stack<IOperation>();
        var root = operation;
        while (true)
        {
            if (root is ILocalFunctionOperation or IAnonymousFunctionOperation) nested.Push(root);
            if (root.Parent == null) break;
            root = root.Parent;
        }
        var graph = CreateGraph(root, cancellationToken);
        foreach (var function in nested)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (function is ILocalFunctionOperation local)
                graph = graph.GetLocalFunctionControlFlowGraph(local.Symbol, cancellationToken);
            else
            {
                var anonymous = (IAnonymousFunctionOperation)function;
                var flow = FlowOperations(graph, cancellationToken).OfType<IFlowAnonymousFunctionOperation>()
                    .FirstOrDefault(candidate => SameSymbol(candidate.Symbol, anonymous.Symbol))
                    ?? throw new ArgumentException("Roslyn did not produce a control-flow body for this anonymous function.", nameof(offset));
                graph = graph.GetAnonymousFunctionControlFlowGraph(flow, cancellationToken);
            }
        }

        var collector = new OperationCollector(maximumDepth, maximumNodes, cancellationToken);
        var symbols = new InspectionSymbols(this, maximumSymbols, collector.Text, cancellationToken);
        var regionIds = new Dictionary<ControlFlowRegion, int>(); var allRegions = new List<ControlFlowRegion>();
        var pendingRegions = new Stack<ControlFlowRegion>(); pendingRegions.Push(graph.Root);
        while (pendingRegions.Count != 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var region = pendingRegions.Pop(); regionIds.Add(region, allRegions.Count); allRegions.Add(region);
            foreach (var child in region.NestedRegions.Reverse()) pendingRegions.Push(child);
        }
        var regions = allRegions.Take(maximumRegions).Select(region =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var locals = symbols.AddSet(region.Locals.Cast<ISymbol>().ToImmutableArray());
            var functions = symbols.AddSet(region.LocalFunctions.Cast<ISymbol>().ToImmutableArray());
            // A nested function's root can point into its parent's graph. Region IDs
            // belong to this result; ParentBody identifies the enclosing graph.
            return new CSharpFlowRegion(regionIds[region], region.EnclosingRegion != null && regionIds.TryGetValue(region.EnclosingRegion, out var parentId) ? parentId : null,
                region.Kind.ToString(), collector.Text.Get(region.ExceptionType?.ToDisplayString()), region.FirstBlockOrdinal,
                region.LastBlockOrdinal, locals, functions, region.CaptureIds.Length,
                region.CaptureIds.Take(256).Select(collector.Capture).ToImmutableArray(),
                region.CaptureIds.Length > 256 || locals.Truncated || functions.Truncated);
        }).ToImmutableArray();
        var blocks = ImmutableArray.CreateBuilder<CSharpFlowBlock>();
        foreach (var block in graph.Blocks.Take(maximumBlocks))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var operations = ImmutableArray.CreateBuilder<int>();
            foreach (var item in block.Operations)
            { var id = collector.Add(item); if (id == null) break; operations.Add(id.Value); }
            var branchValue = block.BranchValue == null ? null : collector.Add(block.BranchValue);
            var fallThrough = Branch(block.FallThroughSuccessor); var conditional = Branch(block.ConditionalSuccessor);
            blocks.Add(new(block.Ordinal, block.Kind.ToString(), block.IsReachable, regionIds[block.EnclosingRegion],
                block.ConditionKind.ToString(), block.Operations.Length, operations.ToImmutable(), branchValue,
                fallThrough, conditional, block.Predecessors.Length,
                block.Predecessors.Take(128).Select(branch => branch.Source.Ordinal).ToImmutableArray(),
                operations.Count < block.Operations.Length || block.BranchValue != null && branchValue == null ||
                block.Predecessors.Length > 128 || fallThrough?.Truncated == true || conditional?.Truncated == true));
        }
        return new(path, Location(tree, graph.OriginalOperation.Syntax.Span, false, cancellationToken),
            graph.Parent == null ? null : Location(tree, graph.Parent.OriginalOperation.Syntax.Span, false, cancellationToken),
            graph.Blocks.Length, allRegions.Count, blocks.ToImmutable(), regions, collector.Nodes.ToImmutableArray(), symbols.Items.ToImmutableArray(),
            graph.Blocks.Length > maximumBlocks || allRegions.Count > maximumRegions || collector.Truncated || symbols.Truncated ||
            blocks.Any(block => block.Truncated) || regions.Any(region => region.Truncated));

        CSharpFlowBranch? Branch(ControlFlowBranch? branch) => branch == null ? null : new(branch.Destination?.Ordinal,
            branch.Semantics.ToString(), branch.IsConditionalSuccessor,
            branch.LeavingRegions.Take(64).Select(region => regionIds[region]).ToImmutableArray(),
            branch.EnteringRegions.Take(64).Select(region => regionIds[region]).ToImmutableArray(),
            branch.FinallyRegions.Take(64).Select(region => regionIds[region]).ToImmutableArray(),
            branch.LeavingRegions.Length > 64 || branch.EnteringRegions.Length > 64 || branch.FinallyRegions.Length > 64);
    }

    public CSharpDataFlowResult GetDataFlow(string path, int offset, int length = 0, int maximumSymbols = 1000,
        CancellationToken cancellationToken = default)
    {
        Bound(maximumSymbols); var tree = Tree(path);
        var selected = InspectionSelection(tree, offset, length, cancellationToken);
        var model = _compilation.GetSemanticModel(tree);
        DataFlowAnalysis? analysis = null; Span analyzed = default; string? kind = null;
        if (length > 0 && selected.Span != new Span(offset, length))
        {
            var statements = selected switch
            {
                BlockSyntax block => block.Statements.AsEnumerable(),
                SwitchSectionSyntax section => section.Statements.AsEnumerable(),
                _ => Enumerable.Empty<StatementSyntax>()
            };
            var intersecting = statements.Where(statement => statement.Span.OverlapsWith(new Span(offset, length))).ToArray();
            if (intersecting.Length > 0)
            {
                analysis = model.AnalyzeDataFlow(intersecting[0], intersecting[intersecting.Length - 1]);
                analyzed = Span.FromBounds(intersecting[0].SpanStart, intersecting[intersecting.Length - 1].Span.End);
                kind = "StatementRange";
            }
        }
        if (analysis == null)
        {
            SyntaxNode? region = null;
            foreach (var node in selected.AncestorsAndSelf())
            {
                cancellationToken.ThrowIfCancellationRequested();
                region = node switch
                {
                    ExpressionSyntax or StatementSyntax or ConstructorInitializerSyntax or PrimaryConstructorBaseTypeSyntax => node,
                    BaseMethodDeclarationSyntax method => (SyntaxNode?)method.Body ?? method.ExpressionBody?.Expression,
                    AccessorDeclarationSyntax accessor => (SyntaxNode?)accessor.Body ?? accessor.ExpressionBody?.Expression,
                    PropertyDeclarationSyntax property => property.ExpressionBody?.Expression,
                    ArrowExpressionClauseSyntax arrow => arrow.Expression,
                    _ => null
                };
                // A local-function declaration itself is not a data-flow region; inspect its body.
                if (region is LocalFunctionStatementSyntax local) region = (SyntaxNode?)local.Body ?? local.ExpressionBody?.Expression;
                if (region != null) break;
            }
            analysis = region switch
            {
                ExpressionSyntax expression => model.AnalyzeDataFlow(expression),
                StatementSyntax statement => model.AnalyzeDataFlow(statement),
                ConstructorInitializerSyntax initializer => model.AnalyzeDataFlow(initializer),
                PrimaryConstructorBaseTypeSyntax initializer => model.AnalyzeDataFlow(initializer),
                _ => throw new ArgumentException("Select a C# expression, statement range or executable body.", nameof(offset))
            };
            analyzed = region!.Span; kind = region.Kind().ToString();
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (analysis == null) throw new ArgumentException("Roslyn cannot analyze data flow for this region.", nameof(offset));
        var symbols = new InspectionSymbols(this, maximumSymbols, new InspectionText(), cancellationToken);
        var sets = ImmutableDictionary.CreateBuilder<string, CSharpInspectionSymbolSet>(StringComparer.Ordinal);
        if (analysis.Succeeded)
        {
            sets.Add("variablesDeclared", symbols.AddSet(analysis.VariablesDeclared));
            sets.Add("dataFlowsIn", symbols.AddSet(analysis.DataFlowsIn)); sets.Add("dataFlowsOut", symbols.AddSet(analysis.DataFlowsOut));
            sets.Add("definitelyAssignedOnEntry", symbols.AddSet(analysis.DefinitelyAssignedOnEntry));
            sets.Add("definitelyAssignedOnExit", symbols.AddSet(analysis.DefinitelyAssignedOnExit));
            sets.Add("alwaysAssigned", symbols.AddSet(analysis.AlwaysAssigned));
            sets.Add("readInside", symbols.AddSet(analysis.ReadInside)); sets.Add("writtenInside", symbols.AddSet(analysis.WrittenInside));
            sets.Add("readOutside", symbols.AddSet(analysis.ReadOutside)); sets.Add("writtenOutside", symbols.AddSet(analysis.WrittenOutside));
            sets.Add("captured", symbols.AddSet(analysis.Captured)); sets.Add("capturedInside", symbols.AddSet(analysis.CapturedInside));
            sets.Add("capturedOutside", symbols.AddSet(analysis.CapturedOutside)); sets.Add("unsafeAddressTaken", symbols.AddSet(analysis.UnsafeAddressTaken));
            sets.Add("usedLocalFunctions", symbols.AddSet(analysis.UsedLocalFunctions.Cast<ISymbol>().ToImmutableArray()));
        }
        return new(path, Location(tree, analyzed, false, cancellationToken), kind!, analysis.Succeeded,
            sets.ToImmutable(), symbols.Items.ToImmutableArray(), symbols.Truncated);
    }

    private static SyntaxNode InspectionSelection(SyntaxTree tree, int offset, int length, CancellationToken token)
    {
        var root = tree.GetRoot(token);
        if (offset < 0 || offset > root.FullSpan.End || length < 0 || length > root.FullSpan.End - offset)
            throw new ArgumentOutOfRangeException(nameof(offset), "Use a UTF-16 range within the selected C# document.");
        return length == 0 ? Token(tree, offset, token).Parent ?? root
            : root.FindNode(new Span(offset, length), getInnermostNodeForTie: true);
    }
    private static IOperation? EnclosingOperation(SemanticModel model, SyntaxNode node, CancellationToken token)
    {
        foreach (var ancestor in node.AncestorsAndSelf())
        { token.ThrowIfCancellationRequested(); if (model.GetOperation(ancestor, token) is { } operation) return operation; }
        return null;
    }
    private static ControlFlowGraph CreateGraph(IOperation root, CancellationToken token) => root switch
    {
        IBlockOperation body => ControlFlowGraph.Create(body, token),
        IMethodBodyOperation body => ControlFlowGraph.Create(body, token),
        IConstructorBodyOperation body => ControlFlowGraph.Create(body, token),
        IFieldInitializerOperation initializer => ControlFlowGraph.Create(initializer, token),
        IPropertyInitializerOperation initializer => ControlFlowGraph.Create(initializer, token),
        IParameterInitializerOperation initializer => ControlFlowGraph.Create(initializer, token),
        IAttributeOperation attribute => ControlFlowGraph.Create(attribute, token),
        _ => throw new ArgumentException("Roslyn does not expose a control-flow graph for this operation root: " + root.Kind)
    };
    private static IEnumerable<IOperation> FlowOperations(ControlFlowGraph graph, CancellationToken token)
    {
        var pending = new Stack<IOperation>();
        foreach (var block in graph.Blocks)
        {
            if (block.BranchValue != null) pending.Push(block.BranchValue);
            foreach (var operation in block.Operations) pending.Push(operation);
            while (pending.Count != 0)
            {
                token.ThrowIfCancellationRequested(); var operation = pending.Pop(); yield return operation;
                foreach (var child in operation.ChildOperations) pending.Push(child);
            }
        }
    }
    private static void DepthBound(int value)
    { if (value is < 0 or > 64) throw new ArgumentOutOfRangeException(nameof(value), "Choose a depth between 0 and 64."); }

    private sealed class InspectionText
    {
        private int _remaining = 524288;
        public bool Truncated { get; private set; }
        public string? Get(string? value, int limit = 1024)
        {
            if (value == null) return null;
            var count = Math.Min(value.Length, Math.Min(limit, _remaining)); _remaining -= count;
            if (count < value.Length) Truncated = true;
            return count == value.Length ? value : value.Substring(0, count);
        }
    }
    private sealed class InspectionSymbols(CSharpLanguageService owner, int maximum, InspectionText text, CancellationToken token)
    {
        private readonly Dictionary<ISymbol, int> _ids = new(SymbolEqualityComparer.Default);
        private bool _truncated;
        public List<CSharpInspectionSymbol> Items { get; } = new();
        public bool Truncated => _truncated || text.Truncated;
        public CSharpInspectionSymbolSet AddSet(ImmutableArray<ISymbol> values)
        {
            var ids = ImmutableArray.CreateBuilder<int>();
            foreach (var symbol in values)
            {
                token.ThrowIfCancellationRequested();
                if (!_ids.TryGetValue(symbol, out var id))
                {
                    if (Items.Count == maximum) { _truncated = true; continue; }
                    id = Items.Count; _ids.Add(symbol, id);
                    var locations = symbol.Locations.Where(location => location.IsInSource && location.SourceTree != null).Take(17).ToArray();
                    if (locations.Length > 16) _truncated = true;
                    Items.Add(new(id, text.Get(symbol.Name)!, symbol.Kind.ToString(), text.Get(symbol.ToDisplayString())!,
                        text.Get(SymbolType(symbol)?.ToDisplayString()), symbol.DeclaredAccessibility.ToString(), symbol.IsStatic, symbol.IsImplicitlyDeclared,
                        locations.Take(16).Select(location => owner.Location(location.SourceTree!, location.SourceSpan, true, token)).ToImmutableArray(), locations.Length > 16));
                }
                ids.Add(id);
            }
            return new(values.Length, ids.ToImmutable(), ids.Count < values.Length);
        }
    }
    private sealed class OperationCollector(int maximumDepth, int maximumNodes, CancellationToken token)
    {
        private readonly Dictionary<CaptureId, int> _captures = new();
        private bool _truncated;
        public InspectionText Text { get; } = new();
        public List<CSharpOperationNode> Nodes { get; } = new();
        public bool Truncated => _truncated || Text.Truncated;
        public int Capture(CaptureId capture)
        { if (!_captures.TryGetValue(capture, out var id)) { id = _captures.Count; _captures.Add(capture, id); } return id; }
        public int? Add(IOperation operation, int? parent = null, int depth = 0)
        {
            token.ThrowIfCancellationRequested();
            if (Nodes.Count == maximumNodes) { _truncated = true; return null; }
            var id = Nodes.Count; var details = ImmutableDictionary.CreateBuilder<string, object?>(StringComparer.Ordinal);
            var symbol = operation switch
            {
                IInvocationOperation invocation => invocation.TargetMethod,
                IObjectCreationOperation creation => creation.Constructor,
                IMemberReferenceOperation member => member.Member,
                ILocalReferenceOperation local => local.Local,
                IParameterReferenceOperation parameter => parameter.Parameter,
                IVariableDeclaratorOperation variable => variable.Symbol,
                IAnonymousFunctionOperation function => function.Symbol,
                ILocalFunctionOperation function => function.Symbol,
                IFlowAnonymousFunctionOperation function => function.Symbol,
                IArgumentOperation argument => argument.Parameter,
                IConversionOperation conversion => conversion.OperatorMethod,
                IBinaryOperation binary => binary.OperatorMethod,
                IUnaryOperation unary => unary.OperatorMethod,
                ICompoundAssignmentOperation compound => compound.OperatorMethod,
                IIncrementOrDecrementOperation increment => increment.OperatorMethod,
                _ => (ISymbol?)null
            };
            switch (operation)
            {
                case IInvocationOperation invocation: details.Add("isVirtual", invocation.IsVirtual); break;
                case IConversionOperation conversion:
                    Conversion("conversion", conversion.Conversion); details.Add("isTryCast", conversion.IsTryCast); details.Add("isChecked", conversion.IsChecked); break;
                case IArgumentOperation argument:
                    details.Add("argumentKind", argument.ArgumentKind.ToString()); Conversion("inConversion", argument.InConversion); Conversion("outConversion", argument.OutConversion); break;
                case IBinaryOperation binary: Operator(binary.OperatorKind.ToString(), binary.IsChecked, binary.IsLifted); break;
                case IUnaryOperation unary: Operator(unary.OperatorKind.ToString(), unary.IsChecked, unary.IsLifted); break;
                case ICompoundAssignmentOperation compound:
                    Operator(compound.OperatorKind.ToString(), compound.IsChecked, compound.IsLifted); Conversion("inConversion", compound.InConversion); Conversion("outConversion", compound.OutConversion); break;
                case IIncrementOrDecrementOperation increment:
                    details.Add("isPostfix", increment.IsPostfix); details.Add("isChecked", increment.IsChecked); details.Add("isLifted", increment.IsLifted); break;
                case IFlowCaptureOperation capture: details.Add("captureId", Capture(capture.Id)); break;
                case IFlowCaptureReferenceOperation capture: details.Add("captureId", Capture(capture.Id)); details.Add("isInitialization", capture.IsInitialization); break;
            }
            var constant = operation.ConstantValue.HasValue ? ConstantValue(operation.ConstantValue.Value) : null;
            if (constant is string value) constant = Text.Get(value);
            var snippetLength = Math.Min(operation.Syntax.Span.Length, 256);
            var snippet = operation.Syntax.SyntaxTree.GetText(token).ToString(new(operation.Syntax.SpanStart, snippetLength));
            if (snippetLength < operation.Syntax.Span.Length) details.Add("syntaxTruncated", true);
            Nodes.Add(new(id, parent, depth, operation.Kind.ToString(), new(operation.Syntax.SpanStart, operation.Syntax.Span.Length),
                Text.Get(operation.Type?.ToDisplayString()), Text.Get(symbol?.ToDisplayString()), operation.IsImplicit,
                operation.ConstantValue.HasValue, constant, Text.Get(snippet, 256)!, details.ToImmutable(), operation.ChildOperations.Count,
                ImmutableArray<int>.Empty, false));
            var children = ImmutableArray.CreateBuilder<int>();
            if (depth < maximumDepth)
                foreach (var child in operation.ChildOperations)
                { var childId = Add(child, id, depth + 1); if (childId == null) break; children.Add(childId.Value); }
            var omitted = children.Count < operation.ChildOperations.Count;
            if (omitted) _truncated = true;
            Nodes[id] = Nodes[id] with { Children = children.ToImmutable(), ChildrenTruncated = omitted };
            return id;
            void Operator(string kind, bool isChecked, bool isLifted)
            { details.Add("operator", kind); details.Add("isChecked", isChecked); details.Add("isLifted", isLifted); }
            void Conversion(string name, CommonConversion conversion) => details.Add(name, new
            {
                conversion.Exists, conversion.IsIdentity, conversion.IsImplicit, conversion.IsNumeric,
                conversion.IsReference, conversion.IsNullable, conversion.IsUserDefined,
                Method = Text.Get(conversion.MethodSymbol?.ToDisplayString())
            });
        }
    }
}
