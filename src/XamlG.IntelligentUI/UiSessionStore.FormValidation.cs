namespace XamlG.IntelligentUI;

public sealed partial class UiSessionStore
{
    /// <summary>Run one trusted asynchronous validator against an exact session/source/state snapshot.
    /// Input, source, data, release or workspace changes cancel the wait and invalidate its result.</summary>
    public async Task<UiSnapshot> ValidateFormAsync(UiFormCall request, string principal,
        CancellationToken cancellationToken = default, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(request); cancellationToken.ThrowIfCancellationRequested();
        var duration = timeout ?? TimeSpan.FromSeconds(10);
        if (duration <= TimeSpan.Zero || duration > TimeSpan.FromMinutes(1)) throw new ArgumentOutOfRangeException(nameof(timeout));
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lifetime.CancelAfter(duration);
        UiSnapshot pending; UiFormInteraction form; UiAsyncFormValidator validator;
        Action<UiSnapshot>? changed = null; Action<string>? released = null;
        lock (_gate)
        {
            var entry = Get(request.Id, principal); Revisions(entry.Snapshot, request.ExpectedRevision, request.ExpectedStateRevision);
            form = FindForm(entry.Snapshot, request.FormKey);
            if (form.Validator == null || !_formValidators.TryGetValue(form.Validator, out validator!))
                throw new UiException("unknown_validator", "The form validator is not registered by this application.");
            if (form.Pending) throw new UiException("validation_pending", "Validation is already running for this form.");
            if (form.Error != null || form.Fields.Any(field => field.Error != null))
                throw new UiException("invalid_form", "Correct the synchronous field errors first.");
            form = form with { Submitted = true, Pending = true, Validated = false, AsyncError = null, ValidationId = Guid.NewGuid().ToString("N") };
            pending = CommitForm(entry, form);
            changed = value =>
            {
                if (value.Id == pending.Id && (value.SessionId != pending.SessionId || value.Revision != pending.Revision || value.StateRevision != pending.StateRevision)) lifetime.Cancel();
            };
            released = id => { if (id == pending.Id) lifetime.Cancel(); };
            Changed += changed; Released += released;
        }
        Notify(pending);
        string? error = null;
        try
        {
            var context = new UiFormValidationContext(principal, pending.Id, request.FormKey, pending.State, pending.Data, form.Fields);
            error = await validator(context, lifetime.Token).AsTask().WaitAsync(lifetime.Token).ConfigureAwait(false);
            if (error?.Length > Compiler.Limits.TextCharacters) error = "The validator returned an oversized result.";
            if (string.IsNullOrWhiteSpace(error)) error = null;
        }
        catch (OperationCanceledException)
        {
            error = cancellationToken.IsCancellationRequested ? "Validation cancelled." : "Validation timed out or its input changed.";
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // A service exception is not a safe user-facing validation result.
            error = "The application validator failed. Try again.";
        }
        finally { Changed -= changed; Released -= released; }

        UiSnapshot completed;
        lock (_gate)
        {
            var entry = Get(request.Id, principal);
            if (entry.Snapshot.SessionId != pending.SessionId) throw new UiException("revision_conflict", "The validation session was replaced.");
            Revisions(entry.Snapshot, pending.Revision, pending.StateRevision);
            var current = FindForm(entry.Snapshot, request.FormKey);
            if (current.ValidationId != form.ValidationId) throw new UiException("revision_conflict", "The validation request is obsolete.");
            completed = CommitForm(entry, current with { Pending = false, Validated = true, AsyncError = error, ValidationId = null });
        }
        Notify(completed);
        cancellationToken.ThrowIfCancellationRequested();
        return completed;
    }

    public Task<UiSnapshot> ValidateFormLocalAsync(UiFormCall request, CancellationToken cancellationToken = default)
        => ValidateFormAsync(request, LocalPrincipal(request.Id), cancellationToken);
}
