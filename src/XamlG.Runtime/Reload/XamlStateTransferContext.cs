namespace XamlG.Runtime.Reload;

public sealed record XamlStateTransferContext(XamlRuntimeNode Previous, XamlRuntimeNode Candidate)
{
    public bool IsDeclarationUnchanged(string member)
    {
        var oldSource = Previous.Source;
        var newSource = Candidate.Source;
        if (oldSource == null || newSource == null) return false;
        var oldExists = oldSource.Declarations.TryGetValue(member, out var oldValue);
        var newExists = newSource.Declarations.TryGetValue(member, out var newValue);
        return oldExists == newExists && (!oldExists || oldValue == newValue);
    }
    public bool HasNoDeclaration(string member) => Previous.Source != null && Candidate.Source != null &&
        !Previous.Source.Declarations.ContainsKey(member) && !Candidate.Source.Declarations.ContainsKey(member);
}
