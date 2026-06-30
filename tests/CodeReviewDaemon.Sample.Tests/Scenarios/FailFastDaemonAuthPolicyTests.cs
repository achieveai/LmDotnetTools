using AchieveAi.LmDotnetTools.LmAgentInfra.Auth;
using AchieveAi.LmDotnetTools.LmTestUtils.Logging;
using CodeReviewDaemon.Sample.Auth;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace CodeReviewDaemon.Sample.Tests.Scenarios;

/// <summary>
/// P2.3 — the unattended daemon's auth-resolution policy. Where the chat app would hold the webhook
/// call open for an interactive sign-in, the daemon has no human in the loop, so it must fail fast:
/// deny immediately AND raise an operator "auth required" signal (locked architecture point 5). This
/// is exactly what the config-only path (<c>HoldTimeoutSeconds = 0</c>) could not do — it denies
/// silently.
/// </summary>
public sealed class FailFastDaemonAuthPolicyTests : LoggingTestBase
{
    public FailFastDaemonAuthPolicyTests(ITestOutputHelper output)
        : base(output)
    {
    }

    [Fact]
    public async Task Resolve_denies_immediately_by_returning_null()
    {
        var notifier = new RecordingAuthEventNotifier();
        var policy = new FailFastDaemonAuthPolicy(notifier, LoggerFactory.CreateLogger<FailFastDaemonAuthPolicy>());

        var result = await policy.ResolveAsync(new StubProvider("github"), scopes: null, CancellationToken.None);

        result.Should().BeNull("the unattended daemon cannot complete an interactive sign-in, so it denies");
    }

    [Fact]
    public async Task Resolve_raises_the_operator_auth_required_signal()
    {
        var notifier = new RecordingAuthEventNotifier();
        var policy = new FailFastDaemonAuthPolicy(notifier, LoggerFactory.CreateLogger<FailFastDaemonAuthPolicy>());

        _ = await policy.ResolveAsync(new StubProvider("ado"), scopes: null, CancellationToken.None);

        notifier.Required.Should().ContainSingle()
            .Which.ProviderId.Should().Be("ado", "the signal must name the provider that needs sign-in");
        notifier.Denied.Should().BeEmpty("deny is the return value, not a separate notification");
        notifier.Completed.Should().BeEmpty();
    }

    /// <summary>Minimal provider whose only meaningful surface for the policy is <see cref="ProviderId"/>.</summary>
    private sealed class StubProvider(string providerId) : IOAuthTokenProvider
    {
        public string ProviderId { get; } = providerId;
        public OAuthStatus Status => new(OAuthSignInState.NotStarted, null, [], null, null);
        public Task HydrateFromStoreAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<SignInChallenge> BeginSignInAsync(CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task SignOutAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<OAuthAccessToken> GetAccessTokenAsync(IReadOnlyList<string>? scopes = null, CancellationToken ct = default) =>
            throw new InvalidOperationException("not signed in");
    }
}
