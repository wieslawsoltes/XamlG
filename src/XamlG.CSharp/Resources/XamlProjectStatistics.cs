namespace XamlG.CSharp.Resources;

/// <summary>Actual compiler work counters for this project snapshot, not wall-clock benchmark estimates.</summary>
public sealed record XamlProjectStatistics(int BoundDocuments, int ReusedBindings, int EmittedDocuments, int ReusedOutputs);
