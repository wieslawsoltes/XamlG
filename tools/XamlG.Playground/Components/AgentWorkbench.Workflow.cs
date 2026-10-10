using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace XamlG.Playground.Components;

public partial class AgentWorkbench
{
    private static readonly Dictionary<string, ComposerSubmission> Submissions = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, long> DraftVersions = new(StringComparer.Ordinal);
    private bool _sending, _showGoalEditor, _goalBusy;
    private string _goalObjective = "", _goalBudget = "";
    private bool CanSend => Selected is { IsPreviousWorkspace: false } task && task.Status is not ("cancelled" or "failed") &&
        (!AnyRunning || IsRunning(task)) && ProviderReady && !_modelsBusy && !_sending && !_reviewBusy && !string.IsNullOrWhiteSpace(_draft) && _runReview == null;
    private string ComposerPlaceholder => Selected?.Mode == "plan" ? "Describe what you want to plan…" :
        Selected is { } task && IsRunning(task) ? "Add a follow-up, or steer the current work…" : "Ask the agent to build, fix, or explore…";
    private long NextDraftRevision(string id)
    {
        var revision = Math.Max(DraftVersions.GetValueOrDefault(id), _state.Tasks.FirstOrDefault(task => task.Id == id)?.DraftRevision ?? 0) + 1;
        DraftVersions[id] = revision; return revision;
    }

    private async Task SendDraftAsync(bool steer = false)
    {
        if (!CanSend || Selected == null) return;
        var text = _draft; var originalDraft = text; var id = _selectedId;
        if (!steer && text.Trim() == "/compact") { ReviewCompaction(); return; }
        if (!steer && text.Trim() == "/goal") { OpenGoalEditor(); return; }
        if (!steer && (_profile == "fullAccess" || Preferences.ReviewBeforeSend) && !IsRunning(Selected) && !text.StartsWith("/", StringComparison.Ordinal))
        { ReviewRun(text); return; }
        _sending = true;
        try
        {
            _error = null;
            if (!steer && text.Trim() == "/plan")
            {
                await SetModeCoreAsync(Selected.Mode == "plan" ? "default" : "plan", id);
                await ClearComposerCommandAsync(id, originalDraft); return;
            }
            if (!steer && text.StartsWith("/plan ", StringComparison.Ordinal))
            { await SetModeCoreAsync("plan", id); text = text[6..].Trim(); }
            if (!steer && text.StartsWith("/goal ", StringComparison.Ordinal))
            {
                var command = text[6..].Trim();
                if (command is "pause" or "resume" or "clear")
                {
                    await RequestAsync<JsonElement>("goal_" + command, new { id });
                    await ClearComposerCommandAsync(id, originalDraft); await RefreshAfterCommandAsync();
                    if (command == "resume" && _selectedId == id) await ContinueGoalAsync();
                    return;
                }
                if (Selected?.ActiveGoal?.Objective != command)
                    await RequestAsync<JsonElement>("goal_set", new { id, objective = command });
                text = command;
            }
            if (_selectedId != id) return;
            var options = JsonSerializer.SerializeToElement(Options());
            await SubmitTextAsync(id, text, options, steer ? "steer" : "queue");
            ClearSubmittedDraft(id, originalDraft);
            await RefreshAfterCommandAsync();
            if (Selected?.Id == id && Selected.Status == "paused" && !AnyRunning) await ResumeAsync();
        }
        catch (Exception error) when (error is JSException or JsonException or ArgumentException) { _error = error.Message; }
        finally { _sending = false; await SaveUiStateAsync(); }
    }

    private async Task SubmitTextAsync(string id, string text, JsonElement options, string delivery = "queue", bool fullAccessAcknowledged = false)
    {
        if (!Submissions.TryGetValue(id, out var submission) || submission.Text != text || submission.Delivery != delivery)
            Submissions[id] = submission = new(Guid.NewGuid().ToString("N"), text, delivery, NextDraftRevision(id));
        // Save the request identity before dispatch so a reload can retry the same acceptance.
        await SaveUiStateAsync();
        await RequestAsync<JsonElement>("send", new { id, text, delivery, clientMessageId = submission.Id,
            draftRevision = submission.DraftRevision, options, fullAccessAcknowledged });
        Submissions.Remove(id);
        ClearSubmittedDraft(id, text);
    }

    private static void ClearSubmittedDraft(string id, string text)
    { if (ComposerDrafts.GetValueOrDefault(id) == text) ComposerDrafts[id] = ""; }

    private async Task ClearComposerCommandAsync(string id, string text)
    {
        if (ComposerDrafts.GetValueOrDefault(id) != text) return;
        ClearSubmittedDraft(id, text);
        await RequestAsync<JsonElement>("draft", new { id, text = "", revision = NextDraftRevision(id) });
    }

    private Task ChangeModeAsync(ChangeEventArgs args) => SetModeAsync(args.Value?.ToString() ?? "default");
    private async Task SetModeAsync(string mode)
    {
        try { _error = null; await SetModeCoreAsync(mode); }
        catch (JSException error) { _error = error.Message; }
    }
    private async Task SetModeCoreAsync(string mode, string? id = null)
    {
        id ??= _selectedId;
        await RequestAsync<JsonElement>("mode", new { id, mode });
        if (mode == "default" && _taskPreferences.TryGetValue(id, out var preferences) && preferences.Profile == "plan") preferences.Profile = "ask";
        await RefreshAfterCommandAsync();
    }

    private async Task ResumeAsync()
    {
        if (Selected == null || AnyRunning) return;
        if (_profile == "fullAccess" || Preferences.ReviewBeforeSend) { ReviewRun(null); return; }
        await CommandAsync("run", new { id = _selectedId, options = Options(), confirmed = true });
    }

    private void ImplementPlan()
    {
        if (Selected?.ProposedPlan is not { Accepted: false } plan || AnyRunning) return;
        if (_profile == "plan") _profile = "ask";
        ReviewRun(null, planRevision: plan.Revision);
    }

    private void OpenGoalEditor()
    { _goalObjective = _draft; _goalBudget = ""; _showGoalEditor = true; }
    private async Task StartGoalAsync()
    {
        if (_sending || _goalBusy || Selected == null || string.IsNullOrWhiteSpace(_goalObjective)) return;
        if (_goalBudget.Length > 0 && (!long.TryParse(_goalBudget, out var value) || value <= 0))
        { _error = "Enter a positive token budget or leave it empty."; return; }
        var id = _selectedId; _goalBusy = true;
        try
        {
            _error = null;
            await RequestAsync<JsonElement>("goal_set", new { id, objective = _goalObjective,
                tokenBudget = _goalBudget.Length == 0 ? (long?)null : long.Parse(_goalBudget) });
            if (_selectedId != id) return;
            _showGoalEditor = false; _draft = _goalObjective;
            await RefreshAfterCommandAsync(); await SendDraftAsync();
        }
        catch (JSException error) { _error = error.Message; }
        finally { _goalBusy = false; }
    }
    private async Task ResumeGoalAsync()
    {
        try
        {
            _error = null;
            await RequestAsync<JsonElement>("goal_resume", new { id = _selectedId,
                tokenBudget = string.IsNullOrWhiteSpace(_goalBudget) ? (long?)null : long.Parse(_goalBudget) });
            await RefreshAfterCommandAsync(); await ContinueGoalAsync();
        }
        catch (Exception error) when (error is JSException or FormatException or OverflowException) { _error = error.Message; }
    }
    private async Task ContinueGoalAsync()
    {
        if (Selected?.Status == "paused") { await ResumeAsync(); return; }
        if (Selected == null || AnyRunning) return;
        if (_profile == "fullAccess") { ReviewRun("Continue working toward the active goal."); return; }
        await SubmitTextAsync(_selectedId, "Continue working toward the active goal.", JsonSerializer.SerializeToElement(Options()));
        await RefreshAfterCommandAsync();
    }
    private async Task SelectThreadAsync(string id) { Select(id); await SelectSectionAsync("Conversation"); }
    private void UseStarter(string text) { _draft = text; _ = _composerElement.FocusAsync(); }
    private sealed record ComposerSubmission(string Id, string Text, string Delivery, long DraftRevision);
    public sealed class ProposedPlanView { public long Revision { get; set; } public string Markdown { get; set; } = ""; public bool Accepted { get; set; } }
    public sealed class GoalView
    {
        public string Objective { get; set; } = ""; public string Status { get; set; } = "";
        public long? TokenBudget { get; set; } public long TokensUsed { get; set; } public int Continuations { get; set; }
        public string? Evidence { get; set; }
    }
}
