using AchieveAi.LmDotnetTools.LmAgentInfra.Auth;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using CodeReviewDaemon.Sample.Workspace.Sandbox;
using Microsoft.Extensions.Logging;

namespace CodeReviewDaemon.Sample.Tests.Workspace;

/// <summary>
/// <see cref="HostGitCredentialsCollector.CollectAsync"/> is the behavior-rich seam behind
/// <c>BuildHostGitCredentialsSource</c>: it gathers one credential per signed-in provider for EVERY host
/// git command, so a fault on one provider must never deny credentials to the others. These tests pin that
/// per-provider isolation (the regression a bare <c>catch (InvalidOperationException)</c> around the whole
/// loop reintroduces), the blank-token filter, the differentiated logging, and cancellation propagation.
/// </summary>
public class HostGitCredentialsCollectorTests
{
    [Fact]
    public async Task CollectAsync_ReturnsMappedTokens_SkippingNotSignedInAndBlank()
    {
        // Revobot's requested shape: one provider returns a token, one is not signed in (InvalidOperationException
        // per the IOAuthTokenProvider contract), one returns a blank token. Only the real token survives, and
        // the not-signed-in skip is logged at Debug (expected, benign) — never Warning.
        var logger = new CapturingLogger();
        var providers = new IOAuthTokenProvider[]
        {
            new FakeOAuthTokenProvider("github", "gh-token"),
            new ThrowingOAuthTokenProvider("ado", new InvalidOperationException("ADO provider is not signed in.")),
            new FakeOAuthTokenProvider("m365", token: "   "),
        };

        var tokens = await HostGitCredentialsCollector.CollectAsync(providers, logger, CancellationToken.None);

        tokens.Should().ContainSingle().Which.Should().Be(new GitProviderToken("github", "gh-token"));
        logger.Entries.Should().ContainSingle(e => e.Message.Contains("ado"))
            .Which.Level.Should().Be(LogLevel.Debug, "not-signed-in is the expected, benign skip");
    }

    [Fact]
    public async Task CollectAsync_TransientFaultOnOneProvider_DoesNotDenyOthers()
    {
        // The regression this method exists to prevent: tokens are gathered for ALL providers up front, so a
        // transient ADO fault that is NOT an InvalidOperationException (MSAL 5xx/throttling, a network blip to
        // the token endpoint) must be isolated — it must not abort the github.com credential. It is logged at
        // Warning so the silently-skipped provider leaves a breadcrumb.
        var logger = new CapturingLogger();
        var providers = new IOAuthTokenProvider[]
        {
            new ThrowingOAuthTokenProvider("ado", new HttpRequestException("token endpoint 503")),
            new FakeOAuthTokenProvider("github", "gh-token"),
        };

        var tokens = await HostGitCredentialsCollector.CollectAsync(providers, logger, CancellationToken.None);

        tokens.Should().ContainSingle().Which.Should().Be(new GitProviderToken("github", "gh-token"));
        logger.Entries.Should().ContainSingle(e => e.Message.Contains("ado"))
            .Which.Level.Should().Be(LogLevel.Warning, "an unexpected fault must be traceable, not silent");
    }

    [Fact]
    public async Task CollectAsync_AlreadyCancelled_Throws()
    {
        var providers = new IOAuthTokenProvider[] { new FakeOAuthTokenProvider("github", "gh-token") };
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => HostGitCredentialsCollector.CollectAsync(providers, new CapturingLogger(), cts.Token));
    }

    /// <summary>An <see cref="IOAuthTokenProvider"/> whose token fetch always throws a supplied exception.</summary>
    private sealed class ThrowingOAuthTokenProvider(string providerId, Exception toThrow) : IOAuthTokenProvider
    {
        public string ProviderId { get; } = providerId;

        public OAuthStatus Status => new(OAuthSignInState.Failed, ProviderId, [], null, toThrow.Message);

        public Task HydrateFromStoreAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task<SignInChallenge> BeginSignInAsync(CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task SignOutAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task<OAuthAccessToken> GetAccessTokenAsync(
            IReadOnlyList<string>? scopes = null,
            CancellationToken ct = default) => throw toThrow;
    }

    /// <summary>Records the level + formatted message of every log call so a test can assert on them.</summary>
    private sealed class CapturingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }
}
