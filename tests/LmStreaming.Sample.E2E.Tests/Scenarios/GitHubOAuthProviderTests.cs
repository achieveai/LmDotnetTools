using System.Net;
using System.Text;
using AchieveAi.LmDotnetTools.LmTestUtils;
using AchieveAi.LmDotnetTools.LmTestUtils.Logging;
using FluentAssertions;
using Xunit.Abstractions;

namespace LmStreaming.Sample.E2E.Tests.Scenarios;

/// <summary>
/// Unit tests for <see cref="GitHubOAuthProvider"/> — the browser/loopback web-app-flow provider.
/// These drive the token lifecycle with a fake HTTP handler + a real <see cref="FileOAuthTokenStore"/>
/// (so persistence is exercised too): valid-token reuse, expiry-driven refresh with rotation, the
/// non-expiring (classic OAuth App) path, the not-signed-in error, and sign-in survival across a
/// simulated restart. Ungated (no browser, no network), so they run in CI always. SECURITY: token
/// values used here are fakes; nothing real is logged.
/// </summary>
public sealed class GitHubOAuthProviderTests : LoggingTestBase
{
    private const string TokenEndpointPath = "/login/oauth/access_token";

    public GitHubOAuthProviderTests(ITestOutputHelper output)
        : base(output)
    {
    }

    private static GitHubAuthOptions Options() =>
        new() { ClientId = "test-client", ClientSecret = "test-secret", Scopes = ["repo", "read:org"] };

    private GitHubOAuthProvider NewProvider(IOAuthTokenStore store, HttpClient http) =>
        new(Options(), store, http, LoggerFactory.CreateLogger<GitHubOAuthProvider>());

    private FileOAuthTokenStore NewStore(string dir) =>
        new(dir, LoggerFactory.CreateLogger<FileOAuthTokenStore>());

    private static OAuthTokenRecord Record(
        string refresh,
        string access,
        DateTimeOffset? expiry) =>
        new("github", "octocat", refresh, access, expiry, ["repo", "read:org"]);

    /// <summary>A handler that fails the test if any HTTP call is attempted.</summary>
    private static HttpClient NoHttpExpected() =>
        new(new FakeHttpMessageHandler((_, _) =>
            throw new InvalidOperationException("No HTTP call was expected for this case.")));

    /// <summary>A handler that answers the GitHub token endpoint with the given JSON; captures the body.</summary>
    private static HttpClient TokenEndpoint(string json, CapturedRequestContainer captured) =>
        new(new FakeHttpMessageHandler(async (request, ct) =>
        {
            request.RequestUri!.AbsolutePath.Should().Be(TokenEndpointPath);
            captured.Request = request;
            captured.RequestBody = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
        }));

    [Fact]
    public async Task GetAccessToken_returns_stored_nonexpiring_token_without_calling_github()
    {
        LogTestStart();
        using var temp = new TempDir();
        var store = NewStore(temp.Path);
        // Classic OAuth-App token: no refresh token, far-future (sentinel-like) expiry.
        await store.SaveAsync(Record(refresh: "", access: "tok-stored", expiry: DateTimeOffset.UtcNow.AddYears(50)));
        using var http = NoHttpExpected();
        var provider = NewProvider(store, http);

        var token = await provider.GetAccessTokenAsync();
        Logger.LogInformation("Non-expiring path returned a token of length {Len} (value not logged).", token.Value.Length);

        token.Value.Should().Be("tok-stored");
        LogTestEnd();
    }

    [Fact]
    public async Task GetAccessToken_returns_valid_unexpired_token_without_refresh()
    {
        LogTestStart();
        using var temp = new TempDir();
        var store = NewStore(temp.Path);
        // Has a refresh token, but the access token is comfortably valid → must NOT refresh.
        await store.SaveAsync(Record(refresh: "r1", access: "tok1", expiry: DateTimeOffset.UtcNow.AddHours(1)));
        using var http = NoHttpExpected();
        var provider = NewProvider(store, http);

        var token = await provider.GetAccessTokenAsync();
        token.Value.Should().Be("tok1");
        LogTestEnd();
    }

    [Fact]
    public async Task GetAccessToken_refreshes_when_expired_and_rotates_refresh_token()
    {
        LogTestStart();
        using var temp = new TempDir();
        var store = NewStore(temp.Path);
        await store.SaveAsync(Record(refresh: "r1", access: "old", expiry: DateTimeOffset.UtcNow.AddMinutes(-5)));
        var captured = new CapturedRequestContainer();
        using var http = TokenEndpoint(
            """{"access_token":"new-access","refresh_token":"r2","expires_in":3600,"token_type":"bearer"}""",
            captured);
        var provider = NewProvider(store, http);

        var token = await provider.GetAccessTokenAsync();
        Logger.LogInformation("Refresh grant body contained grant_type=refresh_token: {HasGrant}", captured.RequestBody.Contains("refresh_token"));

        token.Value.Should().Be("new-access");
        captured.RequestBody.Should().Contain("grant_type=refresh_token");

        // The rotated refresh + new access token must be persisted for the next run.
        var persisted = await store.GetAsync("github");
        persisted!.AccessToken.Should().Be("new-access");
        persisted.RefreshToken.Should().Be("r2");
        persisted.AccessTokenExpiresAtUtc.Should().BeAfter(DateTimeOffset.UtcNow.AddMinutes(50));
        LogTestEnd();
    }

    [Fact]
    public async Task GetAccessToken_refresh_preserves_refresh_token_when_not_rotated()
    {
        LogTestStart();
        using var temp = new TempDir();
        var store = NewStore(temp.Path);
        await store.SaveAsync(Record(refresh: "r1", access: "old", expiry: DateTimeOffset.UtcNow.AddMinutes(-5)));
        var captured = new CapturedRequestContainer();
        using var http = TokenEndpoint(
            """{"access_token":"new-access","expires_in":3600,"token_type":"bearer"}""",
            captured);
        var provider = NewProvider(store, http);

        var token = await provider.GetAccessTokenAsync();
        token.Value.Should().Be("new-access");

        var persisted = await store.GetAsync("github");
        persisted!.RefreshToken.Should().Be("r1"); // kept because the grant returned none
        LogTestEnd();
    }

    [Fact]
    public async Task GetAccessToken_throws_when_not_signed_in()
    {
        LogTestStart();
        using var temp = new TempDir();
        var store = NewStore(temp.Path);
        using var http = NoHttpExpected();
        var provider = NewProvider(store, http);

        var act = async () => await provider.GetAccessTokenAsync();
        await act.Should().ThrowAsync<InvalidOperationException>();
        LogTestEnd();
    }

    [Fact]
    public async Task Hydrate_then_GetAccessToken_works_across_a_simulated_restart()
    {
        LogTestStart();
        using var temp = new TempDir();

        // Run 1: a prior process persisted a token.
        var store1 = NewStore(temp.Path);
        await store1.SaveAsync(Record(refresh: "", access: "persisted-token", expiry: DateTimeOffset.UtcNow.AddYears(50)));

        // Run 2 (restart): a brand-new provider + store over the SAME directory.
        var store2 = NewStore(temp.Path);
        using var http = NoHttpExpected();
        var provider = NewProvider(store2, http);

        provider.Status.State.Should().Be(OAuthSignInState.NotStarted); // before hydrate
        await provider.HydrateFromStoreAsync();
        Logger.LogInformation("After hydrate: state={State}, account={Account}", provider.Status.State, provider.Status.Account);

        provider.Status.State.Should().Be(OAuthSignInState.SignedIn);
        provider.Status.Account.Should().Be("octocat");

        var token = await provider.GetAccessTokenAsync();
        token.Value.Should().Be("persisted-token"); // reused, no re-sign-in
        LogTestEnd();
    }

    [Fact]
    public void BuildAuthorizeUrl_contains_pkce_loopback_redirect_scope_and_state()
    {
        LogTestStart();
        var url = GitHubOAuthProvider.BuildAuthorizeUrl(
            clientId: "cid-123",
            redirectUri: "http://127.0.0.1:53117/callback",
            scopes: ["repo", "read:org"],
            state: "state-xyz",
            codeChallenge: "challenge-abc");
        Logger.LogInformation("Authorize URL: {Url}", url);

        url.Should().StartWith("https://github.com/login/oauth/authorize?");
        url.Should().Contain("client_id=cid-123");
        url.Should().Contain("code_challenge=challenge-abc");
        url.Should().Contain("code_challenge_method=S256");
        url.Should().Contain("state=state-xyz");
        url.Should().Contain(Uri.EscapeDataString("http://127.0.0.1:53117/callback"));
        url.Should().Contain(Uri.EscapeDataString("repo read:org"));
        LogTestEnd();
    }

    private sealed class TempDir : IDisposable
    {
        public TempDir() =>
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gh-oauth-test-" + Guid.NewGuid().ToString("N"));

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
