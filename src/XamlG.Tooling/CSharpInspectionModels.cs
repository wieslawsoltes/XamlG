using System.Collections.Immutable;
using XamlG.Syntax;

namespace XamlG.Tooling;

/// <summary>Operation IDs and parent/child links are local to this result. A flat representation
/// avoids serialization depth limits and never exposes Roslyn's cyclic object graphs.</summary>
public sealed record CSharpOperationResult(string Path, TextSpan Selection, bool IsGenerated,
    ImmutableArray<int> Roots, ImmutableArray<CSharpOperationNode> Nodes, bool Truncated);
public sealed record CSharpOperationNode(int Id, int? ParentId, int Depth, string Kind, TextSpan Span,
    string? Type, string? Symbol, bool IsImplicit, bool HasConstant, object? Constant,
    string Syntax, ImmutableDictionary<string, object?> Details, int ChildCount,
    ImmutableArray<int> Children, bool ChildrenTruncated);

public sealed record CSharpControlFlowResult(string Path, CSharpLocation Body, CSharpLocation? ParentBody,
    int TotalBlocks, int TotalRegions, ImmutableArray<CSharpFlowBlock> Blocks,
    ImmutableArray<CSharpFlowRegion> Regions, ImmutableArray<CSharpOperationNode> Operations,
    ImmutableArray<CSharpInspectionSymbol> Symbols, bool Truncated);
public sealed record CSharpFlowBlock(int Ordinal, string Kind, bool IsReachable, int RegionId,
    string ConditionKind, int OperationCount, ImmutableArray<int> Operations, int? BranchValue,
    CSharpFlowBranch? FallThrough, CSharpFlowBranch? Conditional, int PredecessorCount,
    ImmutableArray<int> Predecessors, bool Truncated);
public sealed record CSharpFlowBranch(int? Destination, string Semantics, bool IsConditional,
    ImmutableArray<int> LeavingRegions, ImmutableArray<int> EnteringRegions,
    ImmutableArray<int> FinallyRegions, bool Truncated);
public sealed record CSharpFlowRegion(int Id, int? ParentId, string Kind, string? ExceptionType,
    int FirstBlock, int LastBlock, CSharpInspectionSymbolSet Locals, CSharpInspectionSymbolSet LocalFunctions,
    int CaptureCount, ImmutableArray<int> Captures, bool Truncated);

public sealed record CSharpDataFlowResult(string Path, CSharpLocation Region, string RegionKind,
    bool Succeeded, ImmutableDictionary<string, CSharpInspectionSymbolSet> Sets,
    ImmutableArray<CSharpInspectionSymbol> Symbols, bool Truncated);
public sealed record CSharpInspectionSymbolSet(int TotalCount, ImmutableArray<int> Symbols, bool Truncated);
public sealed record CSharpInspectionSymbol(int Id, string Name, string Kind, string Display,
    string? Type, string Accessibility, bool IsStatic, bool IsImplicit,
    ImmutableArray<CSharpLocation> Locations, bool LocationsTruncated);
