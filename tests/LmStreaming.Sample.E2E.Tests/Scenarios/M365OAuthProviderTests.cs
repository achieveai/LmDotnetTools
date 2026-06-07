using AchieveAi.LmDotnetTools.LmTestUtils.Logging;
using FluentAssertions;
using LmStreaming.Sample.Services.Auth;
using Xunit.Abstractions;

namespace LmStreaming.Sample.E2E.Tests.Scenarios;

/// <summary>
/// Unit tests for <see cref="M365OAuthProvider"/> — the MSAL confidential-client + PKCE Microsoft
/// Graph provider. The token exchange + cache layer is owned by MSAL; these cover the integration
/// surface this project owns: configuration gating (no client id/secret → disabled, not crashing),
/// authorize URL shape (PKCE, offline_access, response_mode), single-use / time-bounded pending
/// sign-in state, and the controller-facing error surface from <c>CompleteSignInAsync</c>. Ungated
/// (no browser, no network) — runs in CI always.
/// </summary>
public sealed class M365OAuthProviderTests : LoggingTestBase
{
    public M365OAuthProviderTests(ITestOutputHelper output)
        : base(output)
    {
    }

    private M365OAuthProvider NewProvider(string? clientId, string? clientSecret, string cacheDir, TimeProvider? time = null) =>
        new(
            new M365AuthOptions
            {
                ClientId = clientId,
                ClientSecret = clientSecret,
                TenantId = "common",
                Scopes = ["User.Read", "Mail.Read", "openid", "profile", "offline_access"],
                RedirectPath = "/auth/m365/callback",
            },
            callbackBaseUrl: "http://localhost:5000",
            tokenCacheFilePath: Path.Combine(cacheDir, "msal-m365.bin"),
            LoggerFactory.CreateLogger<M365OAuthProvider>(),
            time);

    [Fact]
    public async Task Unconfigured_provider_is_disabled_not_crashing()
    {
        LogTestStart();
        using var temp = new TempDir();
        var provider = NewProvider(clientId: null, clientSecret: null, cacheDir: temp.Path);

        provider.IsConfigured.Should().BeFalse();

        // Hydrate must be a safe no-op that leaves the provider NotStarted (host startup calls this).
        await provider.HydrateFromStoreAsync();
        provider.Status.State.Should().Be(OAuthSignInState.NotStarted);

        var beginSignIn = async () => await provider.BeginSignInAsync();
        var getToken = async () => await provider.GetAccessTokenAsync();
        await beginSignIn.Should().ThrowAsync<InvalidOperationException>();
        await getToken.Should().ThrowAsync<InvalidOperationException>();

        // Sign-out tolerates the unconfigured state too.
        await provider.SignOutAsync();
        provider.Status.State.Should().Be(OAuthSignInState.NotStarted);
        LogTestEnd();
    }

    [Fact]
    public async Task Missing_client_secret_disables_provider()
    {
        LogTestStart();
        using var temp = new TempDir();
        var provider = NewProvider(clientId: "1d999ae2-a1f6-44ca-b733-c3df6ce8dc0c", clientSecret: null, cacheDir: temp.Path);

        provider.IsConfigured.Should().BeFalse();
        var beginSignIn = async () => await provider.BeginSignInAsync();
        await beginSignIn.Should().ThrowAsync<InvalidOperationException>();
        LogTestEnd();
    }

    [Fact]
    public async Task Hydrate_with_no_cached_account_leaves_not_started()
    {
        LogTestStart();
        using var temp = new TempDir();
        var provider = NewProvider(
            clientId: "1d999ae2-a1f6-44ca-b733-c3df6ce8dc0c",
            clientSecret: "secret-not-used-in-hydrate",
            cacheDir: temp.Path);

        await provider.HydrateFromStoreAsync();
        Logger.LogInformation("Hydrate with empty MSAL cache left state {State}.", provider.Status.State);
        provider.Status.State.Should().Be(OAuthSignInState.NotStarted);

        var getToken = async () => await provider.GetAccessTokenAsync();
        await getToken.Should().ThrowAsync<InvalidOperationException>();
        LogTestEnd();
    }

    [Fact]
    public void BuildAuthorizeUrl_includes_pkce_offline_access_and_query_response_mode()
    {
        LogTestStart();
        var url = M365OAuthProvider.BuildAuthorizeUrl(
            clientId: "client-1",
            tenantId: "common",
            redirectUri: "http://localhost:5000/auth/m365/callback",
            scopes: ["User.Read", "Mail.Read"],
            state: "test-state-abc",
            codeChallenge: "test-challenge-xyz");
        Logger.LogInformation("Authorize URL: {Url}", url);

        url.Should().StartWith("https://login.microsoftonline.com/common/oauth2/v2.0/authorize");
        url.Should().Contain("client_id=client-1");
        url.Should().Contain("response_type=code");
        url.Should().Contain("response_mode=query");
        url.Should().Contain("code_challenge=test-challenge-xyz");
        url.Should().Contain("code_challenge_method=S256");
        url.Should().Contain("state=test-state-abc");
        // redirect_uri is URL-encoded by QueryHelpers
        url.Should().Contain("redirect_uri=http%3A%2F%2Flocalhost%3A5000%2Fauth%2Fm365%2Fcallback");
        // offline_access must be appended so Entra issues a refresh token (MSAL strips it on
        // AcquireToken* calls but the authorize step requires it explicitly).
        url.Should().Contain("offline_access");
        url.Should().Contain("User.Read");
        url.Should().Contain("Mail.Read");
        LogTestEnd();
    }

    [Fact]
    public void BuildAuthorizeUrl_does_not_duplicate_offline_access()
    {
        LogTestStart();
        var url = M365OAuthProvider.BuildAuthorizeUrl(
            clientId: "c",
            tenantId: "common",
            redirectUri: "http://x/cb",
            scopes: ["User.Read", "offline_access"],
            state: "s",
            codeChallenge: "ch");
        var occurrences = url.Split("offline_access").Length - 1;
        occurrences.Should().Be(1);
        LogTestEnd();
    }

    [Fact]
    public async Task CompleteSignInAsync_returns_state_mismatch_when_state_unknown()
    {
        LogTestStart();
        using var temp = new TempDir();
        var provider = NewProvider(
            clientId: "1d999ae2-a1f6-44ca-b733-c3df6ce8dc0c",
            clientSecret: "secret",
            cacheDir: temp.Path);

        var failure = await provider.CompleteSignInAsync(code: "abc", state: "never-registered");

        failure.Should().Be("state_mismatch");
        provider.Status.State.Should().Be(OAuthSignInState.Failed);
        provider.Status.Error.Should().Be("state_mismatch");
        LogTestEnd();
    }

    [Fact]
    public async Task CompleteSignInAsync_returns_state_mismatch_when_state_missing()
    {
        LogTestStart();
        using var temp = new TempDir();
        var provider = NewProvider("cid", "sec", temp.Path);

        var failure = await provider.CompleteSignInAsync(code: "abc", state: null);
        failure.Should().Be("state_mismatch");
        LogTestEnd();
    }

    [Fact]
    public async Task CompleteSignInAsync_state_is_single_use()
    {
        LogTestStart();
        using var temp = new TempDir();
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var provider = NewProvider("cid", "sec", temp.Path, time);

        provider.RegisterPendingForTest("state-1", "verifier-1", time.GetUtcNow() + TimeSpan.FromMinutes(5));
        provider.HasPending("state-1").Should().BeTrue();

        // First completion consumes the state — exchange will fail (no real MSAL backend) but the
        // pending entry must be removed regardless so a replay can't reuse it.
        _ = await provider.CompleteSignInAsync(code: "code-1", state: "state-1");
        provider.HasPending("state-1").Should().BeFalse();

        // Replay with the same state must be rejected as state_mismatch (single-use guarantee).
        var replay = await provider.CompleteSignInAsync(code: "code-1", state: "state-1");
        replay.Should().Be("state_mismatch");
        LogTestEnd();
    }

    [Fact]
    public async Task CompleteSignInAsync_returns_sign_in_expired_after_ttl()
    {
        LogTestStart();
        using var temp = new TempDir();
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var provider = NewProvider("cid", "sec", temp.Path, time);

        // Register a pending entry that has already expired by the time CompleteSignInAsync runs.
        var pastExpiry = time.GetUtcNow() - TimeSpan.FromSeconds(1);
        provider.RegisterPendingForTest("state-expired", "verifier", pastExpiry);

        var failure = await provider.CompleteSignInAsync(code: "code", state: "state-expired");

        failure.Should().Be("sign_in_expired");
        provider.Status.Error.Should().Be("sign_in_expired");
        LogTestEnd();
    }

    [Fact]
    public async Task CompleteSignInAsync_returns_no_code_when_authorize_returned_no_code()
    {
        LogTestStart();
        using var temp = new TempDir();
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var provider = NewProvider("cid", "sec", temp.Path, time);

        provider.RegisterPendingForTest("state-x", "verifier", time.GetUtcNow() + TimeSpan.FromMinutes(5));

        var failure = await provider.CompleteSignInAsync(code: null, state: "state-x");
        failure.Should().Be("no_code");
        provider.Status.Error.Should().Be("no_code");
        LogTestEnd();
    }

    [Fact]
    public async Task CompleteSignInAsync_returns_not_configured_when_provider_disabled()
    {
        LogTestStart();
        using var temp = new TempDir();
        var provider = NewProvider(clientId: null, clientSecret: null, cacheDir: temp.Path);

        var failure = await provider.CompleteSignInAsync(code: "abc", state: "any");
        failure.Should().Be("not_configured");
        LogTestEnd();
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now;
        public ManualTimeProvider(DateTimeOffset start) => _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) => _now += delta;
    }

    private sealed class TempDir : IDisposable
    {
        public TempDir() =>
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "m365-oauth-test-" + Guid.NewGuid().ToString("N"));

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch
            {
                // best-effort cleanup
            }
        }
    }
}
