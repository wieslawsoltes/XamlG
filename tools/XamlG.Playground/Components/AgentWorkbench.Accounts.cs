using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace XamlG.Playground.Components;

public partial class AgentWorkbench
{
    private const string ChatGptProvider = "openai-chatgpt";
    private string _newAccountLabel = "", _accountLabel = "", _observedAccountId = "";
    private string? _signInLaunchUrl, _signInLaunchId, _accountMessage;
    private bool _rememberNewAccount, _rememberAccount, _accountBusy;
    private AccountView? ActiveAccount => _state.ChatGpt?.Accounts.FirstOrDefault(account => account.Id == _state.ChatGpt.ActiveAccountId);
    private bool SigningIn => _state.ChatGpt?.SignIn?.Status is "waiting" or "exchanging";
    private bool ProviderReady => _provider != ChatGptProvider || ActiveAccount is { SignedIn: true, PlanEnabled: true };
    private static string ProviderLabel(string id) => id == ChatGptProvider ? "ChatGPT account · ChatGPT plan usage" : id + " · API key";
    private void ProviderChanged() { _models = []; _model = ""; }
    private void UpdateAccountState()
    {
        var current = _state.ChatGpt?.ActiveAccountId ?? "";
        if (_observedAccountId != current)
        {
            _observedAccountId = current; _accountLabel = ActiveAccount?.Label ?? ""; _rememberAccount = ActiveAccount?.Remember ?? false;
            if (_provider == ChatGptProvider) ProviderChanged();
        }
        if (_state.ChatGpt?.SignIn is not { Status: "waiting" or "exchanging" } pending || pending.Id != _signInLaunchId)
        { _signInLaunchUrl = null; _signInLaunchId = null; }
    }
    private async Task AccountActionAsync(Func<Task> action)
    {
        if (_accountBusy) return; _accountBusy = true; _error = null; _accountMessage = null;
        try { await action(); await RefreshAsync(); }
        catch (Exception error) when (error is JSException or ArgumentException) { _error = error.Message; }
        finally { _accountBusy = false; }
    }
    private Task BeginAccountSignInAsync(bool returning, bool retryRegistration = false, bool requestPlanConsent = false) => AccountActionAsync(async () =>
    {
        var result = await _module!.InvokeAsync<SignInLaunchView>("beginChatGptSignIn", new
        {
            accountId = returning ? ActiveAccount?.Id : null,
            retrySignInId = retryRegistration ? _state.ChatGpt?.SignIn?.Id : null,
            requestPlanConsent,
            label = returning || string.IsNullOrWhiteSpace(_newAccountLabel) ? null : _newAccountLabel,
            remember = returning ? ActiveAccount?.Remember == true : _rememberNewAccount
        });
        _signInLaunchUrl = result.LaunchUrl; _signInLaunchId = result.Id;
    });
    private Task ChooseAccountAsync(ChangeEventArgs args) => AccountActionAsync(async () =>
    { await RequestAsync<object>("chatgpt_select", new { id = args.Value?.ToString() }); });
    private Task ConfigureAccountAsync() => AccountActionAsync(async () =>
    { await RequestAsync<object>("chatgpt_configure", new { id = ActiveAccount!.Id, label = _accountLabel, remember = _rememberAccount }); });
    private Task SignOutAccountAsync() => AccountActionAsync(async () =>
    {
        var result = await RequestAsync<SignOutView>("chatgpt_sign_out", new { id = ActiveAccount!.Id });
        _accountMessage = result.RemoteRevocationConfirmed ? "Signed out. The renewable session was revoked." :
            "Signed out locally. Remote revocation was not confirmed; disconnect the app in ChatGPT settings.";
    });
    private Task CancelAccountSignInAsync() => AccountActionAsync(async () =>
    { await RequestAsync<object>("chatgpt_cancel_sign_in", new { id = _state.ChatGpt!.SignIn!.Id }); });
    private static string AccountSignInMessage(SignInView signIn) => signIn.Status switch
    {
        "waiting" => "Waiting for sign-in in your browser.", "exchanging" => "Verifying the signed-in account.",
        "completed" => "Sign-in completed.", "cancelled" => "Sign-in cancelled or expired.",
        _ => signIn.ErrorCode switch
        {
            "consent_declined" => "Sign-in was cancelled or plan access was declined.",
            "account_identity_changed" => "The returned account does not match the selected registration. Add it as a separate account.",
            "credential_storage_failed" => "Account storage could not be updated. Check the companion's storage permissions before continuing.",
            _ => "Sign-in failed: " + (signIn.ErrorCode ?? "unknown error").Replace('_', ' ')
        }
    };
    public sealed class AccountStateView
    {
        public string? ActiveAccountId { get; set; }
        public AccountView[] Accounts { get; set; } = [];
        public SignInView? SignIn { get; set; }
        public string? StorageError { get; set; }
    }
    public sealed class AccountView
    {
        public string Id { get; set; } = "";
        public string Label { get; set; } = "";
        public string? Email { get; set; }
        public bool SignedIn { get; set; }
        public bool PlanEnabled { get; set; }
        public bool Remember { get; set; }
        public DateTimeOffset? ExpiresAt { get; set; }
        public AccountFailureView? LastFailure { get; set; }
    }
    public sealed class SignInView
    { public string Id { get; set; } = ""; public string Status { get; set; } = ""; public string? ErrorCode { get; set; } public DateTimeOffset ExpiresAt { get; set; } public bool CanRetryRegistration { get; set; } }
    public sealed class SignInLaunchView { public string Id { get; set; } = ""; public string LaunchUrl { get; set; } = ""; }
    public sealed class SignOutView { public bool RemoteRevocationConfirmed { get; set; } }
    public sealed class AccountBindingView { public string Id { get; set; } = ""; public string Label { get; set; } = ""; }
    public sealed class ModelChoiceView { public string Slug { get; set; } = ""; public string DisplayName { get; set; } = ""; }
    public sealed class AccountFailureView { public string Code { get; set; } = ""; public DateTimeOffset Time { get; set; } public FailureDetailsView Details { get; set; } = new(); }
    public sealed class FailureDetailsView { public int HttpStatus { get; set; } public string ResponseShape { get; set; } = ""; public string? RequestId { get; set; } public string? ErrorParameter { get; set; } }
}
