using System.Collections.Immutable;
using System.Text.Json;

namespace XamlG.IntelligentUI;

/// <summary>Inert form semantics carried by compiled operations, never supplied by action requests.</summary>
public sealed record UiFormAnnotation(string Id, string Role, string? FieldKey = null,
    string? Label = null, string? Error = null, string ErrorMode = "Always", string? Validator = null,
    bool ShowErrors = true, bool AuthorEnabled = true);
public sealed record UiFieldInteraction(string Key, string? StateKey, string Label, JsonElement InitialValue,
    JsonElement Value, bool Touched, bool Dirty, string? Error);
public sealed record UiFormInteraction(string Id, string Stamp, ImmutableArray<UiFieldInteraction> Fields,
    bool Submitted = false, bool Pending = false, bool Validated = false, string? AsyncError = null,
    string? Validator = null, string? Error = null, string? ValidationId = null)
{
    public bool IsDirty => Fields.Any(input => input.Dirty);
    public bool IsTouched => Fields.Any(input => input.Touched);
    public bool IsValid => Error == null && Fields.All(input => input.Error == null) && !Pending &&
        (string.IsNullOrEmpty(Validator) || Validated && AsyncError == null);
    public string? FirstInvalidKey => Fields.FirstOrDefault(input => input.Error != null)?.Key;
}
public sealed record UiFormCall(string Id, long ExpectedRevision, long ExpectedStateRevision, string FormKey, string? FieldKey = null);
public sealed record UiFormSubmission(UiSnapshot Snapshot, UiActionCall? Action, string? FocusKey, bool RequiresValidation);
public sealed record UiFormValidationContext(string Principal, string SurfaceId, string FormKey,
    JsonElement State, JsonElement Data, ImmutableArray<UiFieldInteraction> Fields);
/// <summary>A trusted application registration. Models cannot install delegates or validators.</summary>
public delegate ValueTask<string?> UiAsyncFormValidator(UiFormValidationContext context, CancellationToken cancellationToken);
