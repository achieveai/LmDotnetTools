
namespace LmStreaming.Sample.Tests.Services;

/// <summary>
/// Unit tests for <see cref="PendingAuthCoordinator"/> — the deferred-auth hold that keeps a
/// not-signed-in webhook call open while the user is prompted to sign in. Uses a controllable
/// fake provider (token appears on demand) and a recording notifier.
/// </summary>
public class PendingAuthCoordinatorTests
{
    private sealed class FakeTokenProvider : IOAuthTokenProvider
    {
        private OAuthAccessToken? _token;
        private OAuthStatus _status = new(OAuthSignInState.NotStarted, null, [], null, null);

        public string ProviderId => "github";

        public OAuthStatus Status
        {
            get => _status;
            set => _status = value;
        }

        public void SetToken(OAuthAccessToken? token) => _token = token;

        public Task HydrateFromStoreAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task<SignInChallenge> BeginSignInAsync(CancellationToken ct = default) =>
            Task.FromResult(new SignInChallenge("https://example.test/authorize", false));

        public Task SignOutAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task<OAuthAccessToken> GetAccessTokenAsync(
            IReadOnlyList<string>? scopes = null,
            CancellationToken ct = default) =>
            _token is not null
                ? Task.FromResult(_token)
                : throw new InvalidOperationException("not signed in");
    }

    private sealed class RecordingNotifier : IAuthEventNotifier
    {
        public List<(string ProviderId, string SigninUrl, string Reason)> Required { get; } = [];
        public List<string> Completed { get; } = [];
        public List<string> Denied { get; } = [];

        public Task NotifyAuthRequiredAsync(string providerId, string signinUrl, string reason, CancellationToken ct)
        {
            lock (Required)
            {
                Required.Add((providerId, signinUrl, reason));
            }

            return Task.CompletedTask;
        }

        public Task NotifyAuthCompletedAsync(string providerId, CancellationToken ct)
        {
            lock (Completed)
            {
                Completed.Add(providerId);
            }

            return Task.CompletedTask;
        }

        public Task NotifyAuthDeniedAsync(string providerId, string reason, CancellationToken ct)
        {
            lock (Denied)
            {
                Denied.Add(providerId);
            }

            return Task.CompletedTask;
        }
    }

    private static PendingAuthCoordinator CreateCoordinator(
        RecordingNotifier notifier,
        int holdTimeoutSeconds = 5,
        double pollIntervalSeconds = 0.05) =>
        new(
            notifier,
            new AuthOptions
            {
                Webhook = new WebhookOptions
                {
                    HoldTimeoutSeconds = holdTimeoutSeconds,
                    PollIntervalSeconds = pollIntervalSeconds,
                },
            },
            NullLogger<PendingAuthCoordinator>.Instance);

    private static OAuthAccessToken NewToken() =>
        new("tok-123", DateTimeOffset.UtcNow.AddHours(1));

    [Fact]
    public async Task Token_appearing_mid_wait_resolves_with_token_and_notifies_required_and_completed_once()
    {
        var provider = new FakeTokenProvider();
        var notifier = new RecordingNotifier();
        var coordinator = CreateCoordinator(notifier);

        var waitTask = coordinator.WaitForTokenAsync(provider, null, CancellationToken.None);
        await Task.Delay(120);
        provider.SetToken(NewToken());

        var token = await waitTask;

        token.Should().NotBeNull();
        token!.Value.Should().Be("tok-123");
        notifier.Required.Should().ContainSingle()
            .Which.Should().Be(("github", "/auth/github", "sandbox egress requires sign-in to 'github'"));
        notifier.Completed.Should().ContainSingle().Which.Should().Be("github");
        notifier.Denied.Should().BeEmpty();
    }

    [Fact]
    public async Task Hold_timeout_returns_null_and_notifies_denied()
    {
        var provider = new FakeTokenProvider();
        var notifier = new RecordingNotifier();
        var coordinator = CreateCoordinator(notifier, holdTimeoutSeconds: 1);

        var token = await coordinator.WaitForTokenAsync(provider, null, CancellationToken.None);

        token.Should().BeNull();
        notifier.Required.Should().ContainSingle();
        notifier.Completed.Should().BeEmpty();
        notifier.Denied.Should().ContainSingle().Which.Should().Be("github",
            "a timed-out hold must send the terminal auth_denied frame so the banner dismisses");
        coordinator.Snapshot().Should().BeEmpty("the entry must be cleaned up after the hold ends");
    }

    [Fact]
    public async Task Concurrent_waits_for_same_provider_notify_required_once_and_both_get_token()
    {
        var provider = new FakeTokenProvider();
        var notifier = new RecordingNotifier();
        var coordinator = CreateCoordinator(notifier);

        var wait1 = coordinator.WaitForTokenAsync(provider, null, CancellationToken.None);
        var wait2 = coordinator.WaitForTokenAsync(provider, null, CancellationToken.None);
        await Task.Delay(120);

        coordinator.Snapshot().Should().ContainSingle("both waiters share one pending entry");
        provider.SetToken(NewToken());

        var tokens = await Task.WhenAll(wait1, wait2);

        tokens.Should().AllSatisfy(t => t!.Value.Should().Be("tok-123"));
        notifier.Required.Should().ContainSingle("only the first waiter triggers the prompt");
        notifier.Completed.Should().ContainSingle("only the last waiter out signals completion");
        notifier.Denied.Should().BeEmpty("a hold that obtained a token must not also deny");
    }

    [Fact]
    public async Task Fresh_sign_in_failure_during_hold_denies_early()
    {
        var provider = new FakeTokenProvider();
        var notifier = new RecordingNotifier();
        var coordinator = CreateCoordinator(notifier, holdTimeoutSeconds: 30);

        var waitTask = coordinator.WaitForTokenAsync(provider, null, CancellationToken.None);
        await Task.Delay(120);
        provider.Status = new OAuthStatus(OAuthSignInState.Failed, null, [], null, "user_cancelled");

        var token = await waitTask.WaitAsync(TimeSpan.FromSeconds(5));

        token.Should().BeNull("a failure that occurred after the hold began must deny early");
        notifier.Completed.Should().BeEmpty();
        notifier.Denied.Should().ContainSingle().Which.Should().Be("github");
    }

    [Fact]
    public async Task Pre_existing_failed_status_does_not_block_deferral()
    {
        var provider = new FakeTokenProvider
        {
            Status = new OAuthStatus(OAuthSignInState.Failed, null, [], null, "stale failure from a prior attempt"),
        };
        var notifier = new RecordingNotifier();
        var coordinator = CreateCoordinator(notifier);

        var waitTask = coordinator.WaitForTokenAsync(provider, null, CancellationToken.None);
        await Task.Delay(200);

        waitTask.IsCompleted.Should().BeFalse("a stale Failed status must not deny the hold");

        provider.SetToken(NewToken());
        var token = await waitTask;
        token.Should().NotBeNull();
    }

    [Fact]
    public async Task Hold_timeout_zero_disables_deferral_and_returns_null_without_notifying()
    {
        var provider = new FakeTokenProvider();
        var notifier = new RecordingNotifier();
        var coordinator = CreateCoordinator(notifier, holdTimeoutSeconds: 0);

        var token = await coordinator.WaitForTokenAsync(provider, null, CancellationToken.None);

        token.Should().BeNull();
        notifier.Required.Should().BeEmpty();
        notifier.Completed.Should().BeEmpty();
        notifier.Denied.Should().BeEmpty("disabled deferral returns before entering a hold, so no prompt and no terminal frame");
    }

    [Fact]
    public async Task Gateway_cancellation_throws_and_cleans_up_entry()
    {
        var provider = new FakeTokenProvider();
        var notifier = new RecordingNotifier();
        var coordinator = CreateCoordinator(notifier, holdTimeoutSeconds: 30);

        using var cts = new CancellationTokenSource();
        var waitTask = coordinator.WaitForTokenAsync(provider, null, cts.Token);
        await Task.Delay(120);
        await cts.CancelAsync();

        var act = async () => await waitTask;
        _ = await act.Should().ThrowAsync<OperationCanceledException>();
        coordinator.Snapshot().Should().BeEmpty("a cancelled hold must not leak its pending entry");
        notifier.Completed.Should().BeEmpty();
        // The aborted hold is the last waiter leaving without a token, so it sends auth_denied to
        // dismiss any prompt (a fresh webhook will re-prompt if the gateway retries).
        notifier.Denied.Should().ContainSingle().Which.Should().Be("github");
    }

    [Fact]
    public async Task Snapshot_exposes_pending_hold_for_replay_to_late_connections()
    {
        var provider = new FakeTokenProvider();
        var notifier = new RecordingNotifier();
        var coordinator = CreateCoordinator(notifier);

        var waitTask = coordinator.WaitForTokenAsync(provider, null, CancellationToken.None);
        await Task.Delay(120);

        var snapshot = coordinator.Snapshot();
        snapshot.Should().ContainSingle();
        snapshot[0].ProviderId.Should().Be("github");
        snapshot[0].SigninUrl.Should().Be("/auth/github");

        provider.SetToken(NewToken());
        _ = await waitTask;
        coordinator.Snapshot().Should().BeEmpty();
    }
}
