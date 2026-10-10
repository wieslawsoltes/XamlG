using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using XamlG.Agents;
using XamlG.Agents.OpenAI;
using Xunit;

namespace XamlG.Automation.Tests;

public sealed class ChatGptAccountTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sign_in_uses_pkce_and_validated_identity_and_only_remembered_credentials_survive_restart(bool remember)
    {
        await using var fixture = await ChatGptAccountFixture.StartAsync();
        await using var manager = await fixture.ManagerAsync();
        var account = await fixture.SignInAsync(manager, remember);
        Assert.True(account.SignedIn); Assert.True(account.PlanEnabled); Assert.Equal(remember, account.Remember);
        var authorize = Assert.Single(fixture.Authorizations);
        Assert.Equal("dynamic_agent_client", authorize["client_id"]);
        Assert.StartsWith("urn:uuid:", authorize["ext_agent_host_id"]);
        Assert.Equal("S256", authorize["code_challenge_method"]);
        Assert.Equal("https://api.openai.com/v1", authorize["resource"]);
        Assert.Contains("chatgpt.tokens.use.direct", authorize["scope"]);
        var exchange = Assert.Single(fixture.TokenRequests);
        Assert.Equal("authorization_code", exchange["grant_type"]); Assert.Equal(account.ClientId, exchange["client_id"]);
        Assert.Equal(authorize["redirect_uri"], exchange["redirect_uri"]);
        var provider = manager.CreateProvider(account.Id);
        Assert.Equal(new[] { new ChatGptModel("fixture-z", "First choice"), new ChatGptModel("fixture-a", "Second choice") }, await provider.ListModelChoicesAsync(fixture.Token));
        Assert.Equal("Bearer synthetic_access_1", Assert.Single(fixture.ModelAuthorizations));
        var state = JsonSerializer.Serialize(manager.State);
        Assert.DoesNotContain("synthetic_access_", state); Assert.DoesNotContain("synthetic_refresh_", state);
        Assert.Throws<IOException>(() => new ChatGptFileCredentialStore(fixture.DirectoryPath));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(fixture.DirectoryPath));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(fixture.DirectoryPath, "accounts.dat")));
        }
        await manager.DisposeAsync();
        await using var reopened = await fixture.ManagerAsync();
        var restored = Assert.Single(reopened.State.Accounts);
        Assert.Equal(account.Id, restored.Id); Assert.Equal(account.ClientId, restored.ClientId);
        Assert.Equal(remember, restored.SignedIn); Assert.Equal(account.Id, reopened.State.ActiveAccountId);
        if (!remember) Assert.Equal("sign_in_required", Assert.Throws<ChatGptAccountException>(() => reopened.CreateProvider(account.Id)).Code);
        Assert.Empty(Directory.GetFiles(fixture.DirectoryPath, "*.tmp"));
    }

    [Theory]
    [InlineData("issuer", "invalid_id_token")]
    [InlineData("audience", "invalid_id_token")]
    [InlineData("expired", "invalid_id_token")]
    [InlineData("nonce", "invalid_id_token_nonce")]
    [InlineData("subject", "invalid_id_token_identity")]
    [InlineData("future-issued", "invalid_id_token_identity")]
    [InlineData("missing-issued", "invalid_id_token_identity")]
    [InlineData("authorized-party", "invalid_authorized_party")]
    [InlineData("multiple-audiences", "invalid_authorized_party")]
    public async Task Invalid_identity_never_creates_an_account_and_revokes_the_issued_refresh_token(string fault, string code)
    {
        await using var fixture = await ChatGptAccountFixture.StartAsync(); fixture.IdentityFault = fault;
        await using var manager = await fixture.ManagerAsync();
        var launch = await manager.BeginSignInAsync(null, "Rejected identity", true, ownerSession: fixture.Token, cancellationToken: fixture.Token);
        Assert.Equal(HttpStatusCode.BadRequest, await fixture.CompleteAsync(await fixture.LaunchAsync(launch)));
        await fixture.UntilAsync(() => manager.State.SignIn?.Status == "failed");
        Assert.Equal(code, manager.State.SignIn!.ErrorCode); Assert.Empty(manager.State.Accounts);
        Assert.Equal("synthetic_refresh_1", Assert.Single(fixture.Revocations)["token"]);
        Assert.DoesNotContain("synthetic_access_", await File.ReadAllTextAsync(Path.Combine(fixture.DirectoryPath, "accounts.dat"), fixture.Token));
    }

    [Fact]
    public async Task Callback_rejects_wrong_state_duplicate_parameters_and_reused_launch_tickets_without_exchanging_a_code()
    {
        await using var fixture = await ChatGptAccountFixture.StartAsync();
        await using var manager = await fixture.ManagerAsync();
        using var owner = new CancellationTokenSource();
        var launch = await manager.BeginSignInAsync(null, null, false, ownerSession: owner.Token, cancellationToken: fixture.Token);
        var authorization = await fixture.LaunchAsync(launch);
        using (var reused = await fixture.Http.GetAsync(launch.LaunchUrl, fixture.Token)) Assert.Equal(HttpStatusCode.Forbidden, reused.StatusCode);
        var fields = QueryHelpers.ParseQuery(authorization.Query);
        var callback = fields["redirect_uri"].ToString();
        using (var badState = await fixture.Http.GetAsync(callback + "?state=wrong&code=untrusted&client_id=oaiapp_wrong", fixture.Token))
            Assert.Equal(HttpStatusCode.BadRequest, badState.StatusCode);
        var state = Uri.EscapeDataString(fields["state"].ToString());
        await Assert.ThrowsAsync<HttpRequestException>(() => fixture.Http.GetAsync(callback + "?state=" + state + "&state=" + state + "&code=untrusted", fixture.Token));
        using (var host = new HttpRequestMessage(HttpMethod.Get, callback + "?state=" + Uri.EscapeDataString(fields["state"].ToString())))
        {
            host.Headers.Host = "untrusted.example";
            using var denied = await fixture.Http.SendAsync(host, fixture.Token); Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        }
        Assert.Empty(fixture.TokenRequests); Assert.Empty(manager.State.Accounts);
        owner.Cancel(); await fixture.UntilAsync(() => manager.State.SignIn?.Status == "cancelled");
        await Assert.ThrowsAsync<HttpRequestException>(() => fixture.Http.GetAsync(launch.LaunchUrl, fixture.Token));
    }

    [Fact]
    public async Task Plan_consent_reuses_the_registration_and_account_selection_does_not_retarget_a_captured_provider()
    {
        await using var fixture = await ChatGptAccountFixture.StartAsync(); fixture.Scope = "openid profile email offline_access resource.invoke";
        await using var manager = await fixture.ManagerAsync();
        var first = await fixture.SignInAsync(manager);
        Assert.False(first.PlanEnabled);
        var unavailable = await Assert.ThrowsAsync<AgentProviderException>(() => manager.CreateProvider(first.Id).ListModelsAsync(fixture.Token));
        Assert.Equal("chatgpt_plan_permission_required", unavailable.Code); Assert.Empty(fixture.ModelAuthorizations);
        fixture.Scope += " chatgpt.tokens.use.direct";
        await fixture.SignInAsync(manager, id: first.Id, consent: true);
        var returning = fixture.Authorizations.Last();
        Assert.Equal(first.ClientId, returning["client_id"]); Assert.Equal("consent", returning["prompt"]);
        Assert.NotEmpty(returning["id_token_hint"]); Assert.Equal("fixture@example.test", returning["login_hint"]);
        Assert.Single(manager.State.Accounts);
        var captured = manager.CreateProvider(first.Id); var lifetime = captured.GetSessionLifetime();
        var second = await fixture.SignInAsync(manager);
        Assert.NotEqual(first.Id, second.Id); Assert.Equal(second.Id, manager.State.ActiveAccountId);
        await captured.ListModelsAsync(fixture.Token);
        Assert.Equal("Bearer synthetic_access_2", fixture.ModelAuthorizations.Last());
        Assert.Equal(first.Id, captured.AccountId); Assert.False(lifetime.IsCancellationRequested);
        await manager.SignOutAsync(first.Id, fixture.Token);
        Assert.True(lifetime.IsCancellationRequested); Assert.True(manager.State.Accounts.Single(account => account.Id == second.Id).SignedIn);
        Assert.Equal("synthetic_refresh_2", fixture.Revocations.Last()["token"]);
    }

    [Fact]
    public async Task Concurrent_refresh_is_serialized_and_sign_out_revokes_the_rotated_token_without_reusing_the_old_one()
    {
        await using var fixture = await ChatGptAccountFixture.StartAsync(); fixture.InitialExpiry = 1;
        await using var manager = await fixture.ManagerAsync();
        var account = await fixture.SignInAsync(manager, remember: true);
        var provider = manager.CreateProvider(account.Id); var lifetime = provider.GetSessionLifetime();
        fixture.HoldRefresh = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var reading = provider.ListModelsAsync(fixture.Token);
        await fixture.RefreshEntered.Task.WaitAsync(fixture.Token);
        var signOut = manager.SignOutAsync(account.Id, fixture.Token);
        Assert.False(signOut.IsCompleted); fixture.HoldRefresh.SetResult();
        try { await reading; } catch (OperationCanceledException) { }
        Assert.True((await signOut).RemoteRevocationConfirmed);
        Assert.True(lifetime.IsCancellationRequested); Assert.False(Assert.Single(manager.State.Accounts).SignedIn);
        Assert.Equal("synthetic_refresh_1", Assert.Single(fixture.TokenRequests, request => request["grant_type"] == "refresh_token")["refresh_token"]);
        Assert.Equal("synthetic_refresh_2", Assert.Single(fixture.Revocations)["token"]);
        await manager.DisposeAsync();
        await using var reopened = await fixture.ManagerAsync(); Assert.False(Assert.Single(reopened.State.Accounts).SignedIn);
    }

    [Fact]
    public async Task Rotated_credentials_survive_a_temporary_signing_key_failure_and_retry_never_replays_the_old_refresh_token()
    {
        await using var fixture = await ChatGptAccountFixture.StartAsync(); fixture.InitialExpiry = 1;
        await using var manager = await fixture.ManagerAsync();
        var account = await fixture.SignInAsync(manager, remember: true); fixture.RotateSigningKey(); fixture.KeyFailures = 1;
        var provider = manager.CreateProvider(account.Id);
        var failed = await Assert.ThrowsAsync<AgentProviderException>(() => provider.ListModelsAsync(fixture.Token));
        Assert.True(failed.Retryable); Assert.Empty(fixture.ModelAuthorizations);
        Assert.Single(fixture.TokenRequests, request => request["grant_type"] == "refresh_token");
        await manager.DisposeAsync();
        await using var reopened = await fixture.ManagerAsync();
        await reopened.CreateProvider(account.Id).ListModelsAsync(fixture.Token);
        Assert.Equal("Bearer synthetic_access_2", Assert.Single(fixture.ModelAuthorizations));
        Assert.Single(fixture.TokenRequests, request => request["grant_type"] == "refresh_token");
        Assert.True((await reopened.SignOutAsync(account.Id, fixture.Token)).RemoteRevocationConfirmed);
        Assert.Equal("synthetic_refresh_2", Assert.Single(fixture.Revocations)["token"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Terminal_refresh_or_changed_identity_retires_the_account_session(bool identityChanged)
    {
        await using var fixture = await ChatGptAccountFixture.StartAsync(); fixture.InitialExpiry = 1;
        await using var manager = await fixture.ManagerAsync();
        var account = await fixture.SignInAsync(manager, remember: true);
        var provider = manager.CreateProvider(account.Id); var lifetime = provider.GetSessionLifetime();
        if (identityChanged) fixture.RefreshSubject = "another-subject"; else fixture.RefreshError = "invalid_grant";
        var error = await Assert.ThrowsAsync<AgentProviderException>(() => provider.ListModelsAsync(fixture.Token));
        Assert.Equal(identityChanged ? "account_identity_changed" : "invalid_grant", error.Code);
        Assert.True(lifetime.IsCancellationRequested); Assert.False(Assert.Single(manager.State.Accounts).SignedIn);
        Assert.Empty(fixture.ModelAuthorizations);
        if (identityChanged) Assert.Equal("synthetic_refresh_2", Assert.Single(fixture.Revocations)["token"]);
    }

    [Fact]
    public async Task Unconfirmed_remote_revocation_still_clears_local_credentials_and_remember_can_be_revoked()
    {
        await using var fixture = await ChatGptAccountFixture.StartAsync();
        await using var manager = await fixture.ManagerAsync();
        var account = await fixture.SignInAsync(manager, remember: true);
        await manager.ConfigureAsync(account.Id, "Session only", false, fixture.Token);
        Assert.True(Assert.Single(manager.State.Accounts).SignedIn);
        if (!OperatingSystem.IsWindows()) Assert.DoesNotContain("synthetic_access_", await File.ReadAllTextAsync(Path.Combine(fixture.DirectoryPath, "accounts.dat"), fixture.Token));
        fixture.RevocationStatus = 400;
        Assert.False((await manager.SignOutAsync(account.Id, fixture.Token)).RemoteRevocationConfirmed);
        Assert.False(Assert.Single(manager.State.Accounts).SignedIn);
        await manager.DisposeAsync();
        await using var reopened = await fixture.ManagerAsync();
        Assert.Equal("Session only", Assert.Single(reopened.State.Accounts).Label);
        Assert.False(Assert.Single(reopened.State.Accounts).SignedIn);
    }

    [Fact]
    public async Task Concurrent_model_requests_share_one_rotation_and_preserve_identity_when_no_new_id_token_is_issued()
    {
        await using var fixture = await ChatGptAccountFixture.StartAsync(); fixture.InitialExpiry = 1; fixture.OmitRefreshIdentity = true;
        await using var manager = await fixture.ManagerAsync();
        var account = await fixture.SignInAsync(manager);
        var provider = manager.CreateProvider(account.Id); var lifetime = provider.GetSessionLifetime();
        fixture.HoldRefresh = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = provider.ListModelsAsync(fixture.Token);
        await fixture.RefreshEntered.Task.WaitAsync(fixture.Token);
        var rest = Enumerable.Range(0, 7).Select(_ => provider.ListModelsAsync(fixture.Token)).ToArray();
        fixture.HoldRefresh.SetResult(); await Task.WhenAll(rest.Prepend(first));
        Assert.Single(fixture.TokenRequests, request => request["grant_type"] == "refresh_token");
        Assert.Equal(8, fixture.ModelAuthorizations.Count);
        Assert.All(fixture.ModelAuthorizations, token => Assert.Equal("Bearer synthetic_access_2", token));
        Assert.False(lifetime.IsCancellationRequested);
    }

    [Fact]
    public async Task Failed_first_exchange_retries_the_issued_registration_instead_of_creating_another_client()
    {
        await using var fixture = await ChatGptAccountFixture.StartAsync(); fixture.TokenFailures = 1;
        await using var manager = await fixture.ManagerAsync();
        var launch = await manager.BeginSignInAsync(null, "Registration retry", true, ownerSession: fixture.Token, cancellationToken: fixture.Token);
        Assert.Equal(HttpStatusCode.BadRequest, await fixture.CompleteAsync(await fixture.LaunchAsync(launch)));
        await fixture.UntilAsync(() => manager.State.SignIn?.Status == "failed");
        Assert.True(manager.State.SignIn!.CanRetryRegistration); Assert.Empty(manager.State.Accounts);
        var issued = Assert.Single(fixture.Authorizations)["issued_client"];
        var retry = await manager.BeginSignInAsync(null, null, false, retrySignInId: launch.Id, ownerSession: fixture.Token, cancellationToken: fixture.Token);
        Assert.Equal(HttpStatusCode.OK, await fixture.CompleteAsync(await fixture.LaunchAsync(retry)));
        Assert.Equal(issued, fixture.Authorizations.Last()["client_id"]);
        var account = Assert.Single(manager.State.Accounts);
        Assert.Equal(issued, account.ClientId); Assert.True(account.Remember); Assert.Equal("Registration retry", account.Label);
        Assert.All(fixture.TokenRequests, request => Assert.Equal(issued, request["client_id"]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Real_account_provider_keeps_its_registration_and_sign_out_cancels_pending_tool_approval(bool signOut)
    {
        await using var fixture = await ChatGptAccountFixture.StartAsync();
        await using var manager = await fixture.ManagerAsync();
        var account = await fixture.SignInAsync(manager);
        var provider = manager.CreateProvider(account.Id);
        var other = await fixture.SignInAsync(manager);
        Assert.Equal(other.Id, manager.State.ActiveAccountId);
        var effects = 0; var catalog = new AutomationCatalog();
        catalog.Add<EditArgs, string>("edit", "Edit source", AutomationScope.Source, AutomationEffect.Edit,
            (args, _) => { effects++; return ValueTask.FromResult(args.Text); });
        using var harness = new AgentHarness(catalog);
        var task = harness.CreateTask("Account-bound edit", provider, "fixture-z", fixture.Token);
        var approval = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = harness.RunAsync(task.Id, "Edit through the captured account", new(), async (_, token) =>
        {
            approval.TrySetResult();
            if (signOut) await Task.Delay(System.Threading.Timeout.Infinite, token);
            return AgentApproval.AllowOnce;
        }, cancellationToken: fixture.Token);
        await approval.Task.WaitAsync(fixture.Token);
        if (signOut) await manager.SignOutAsync(account.Id, fixture.Token);
        await run;
        Assert.Equal(signOut ? AgentTaskStatus.Paused : AgentTaskStatus.Completed, task.Status);
        Assert.Equal(signOut ? 0 : 1, effects); Assert.Equal(signOut ? 1 : 2, fixture.InferenceRequests.Count);
        Assert.All(fixture.InferenceRequests, request =>
        {
            Assert.Equal("Bearer synthetic_access_1", request.Authorization);
            Assert.False(request.Body.TryGetProperty("max_output_tokens", out _));
            var tools = request.Body.GetProperty("tools");
            Assert.Equal("namespace", tools[0].GetProperty("type").GetString()); Assert.Equal("xamlg", tools[0].GetProperty("name").GetString());
        });
        Assert.True(manager.State.Accounts.Single(item => item.Id == other.Id).SignedIn);
        if (!signOut)
        {
            var continuation = fixture.InferenceRequests.Last().Body.GetProperty("input").GetRawText();
            Assert.Contains("function_call_output", continuation); Assert.Contains("call_edit", continuation); Assert.Contains("changed", continuation);
            Assert.Contains("Done", harness.ExportTranscript(task.Id));
        }
        Assert.DoesNotContain("synthetic_access_", harness.ExportTranscript(task.Id));
    }

    public sealed record EditArgs(string Text);
}
