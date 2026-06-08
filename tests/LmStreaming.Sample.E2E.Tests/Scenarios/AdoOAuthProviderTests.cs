using AchieveAi.LmDotnetTools.LmTestUtils.Logging;
using FluentAssertions;
using LmStreaming.Sample.Services.Auth;
using Xunit.Abstractions;

namespace LmStreaming.Sample.E2E.Tests.Scenarios;

/// <summary>
/// Unit tests for <see cref="AdoOAuthProvider"/> — the MSAL.NET-backed ADO provider. The interactive
/// + silent token lifecycle itself is owned (and heavily tested) by MSAL; these cover the
/// integration surface this project owns: the provider is disabled (not crashing) when unconfigured,
/// hydrate with no cached account leaves it NotStarted, and reserved scopes are stripped before MSAL.
/// Ungated (no browser, no network), so they run in CI always.
/// </summary>
public sealed class AdoOAuthProviderTests : LoggingTestBase
{
    public AdoOAuthProviderTests(ITestOutputHelper output)
        : base(output)
    {
    }

    private AdoOAuthProvider NewProvider(string? clientId, string cacheDir) =>
        new(
            new AdoAuthOptions
            {
                ClientId = clientId,
                TenantId = "common",
                Scopes = ["499b84ac-1321-427f-aa17-267ca6975798/.default", "offline_access"],
            },
            Path.Combine(cacheDir, "msal-ado.bin"),
            LoggerFactory.CreateLogger<AdoOAuthProvider>());

    [Fact]
    public async Task Unconfigured_provider_is_disabled_not_crashing()
    {
        LogTestStart();
        using var temp = new TempDir();
        var provider = NewProvider(clientId: "", cacheDir: temp.Path);

        // Hydrate must be a safe no-op that leaves the provider NotStarted (host startup calls this).
        await provider.HydrateFromStoreAsync();
        provider.Status.State.Should().Be(OAuthSignInState.NotStarted);

        // Sign-in and token requests surface a clear configuration error rather than NRE.
        var beginSignIn = async () => await provider.BeginSignInAsync();
        var getToken = async () => await provider.GetAccessTokenAsync();
        await beginSignIn.Should().ThrowAsync<InvalidOperationException>();
        await getToken.Should().ThrowAsync<InvalidOperationException>();

        // Sign-out is tolerant even when never configured/signed in.
        await provider.SignOutAsync();
        provider.Status.State.Should().Be(OAuthSignInState.NotStarted);
        LogTestEnd();
    }

    [Fact]
    public async Task Hydrate_with_no_cached_account_leaves_not_started()
    {
        LogTestStart();
        using var temp = new TempDir();
        // Configured (builds a real MSAL app), but there is no cache file → no account.
        var provider = NewProvider(clientId: "0d50963b-7bb9-4fe7-94c7-a99af00b5136", cacheDir: temp.Path);

        await provider.HydrateFromStoreAsync();
        Logger.LogInformation("Hydrate with empty MSAL cache left state {State}.", provider.Status.State);

        provider.Status.State.Should().Be(OAuthSignInState.NotStarted);

        // With no signed-in account, a token request must fail clearly (gateway would then deny).
        var getToken = async () => await provider.GetAccessTokenAsync();
        await getToken.Should().ThrowAsync<InvalidOperationException>();
        LogTestEnd();
    }

    [Fact]
    public void StripReservedScopes_removes_offline_access_keeps_resource_scope()
    {
        LogTestStart();
        var result = OAuthProviderBase.StripReservedScopes(
            ["499b84ac-1321-427f-aa17-267ca6975798/.default", "offline_access", "openid", "profile"]);
        Logger.LogInformation("Stripped scopes: [{Scopes}]", string.Join(", ", result));

        result.Should().ContainSingle().Which.Should().Be("499b84ac-1321-427f-aa17-267ca6975798/.default");
        LogTestEnd();
    }

    private sealed class TempDir : IDisposable
    {
        public TempDir() =>
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ado-oauth-test-" + Guid.NewGuid().ToString("N"));

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
