using AchieveAi.LmDotnetTools.LmAgentInfra.Auth;

namespace CodeReviewDaemon.Sample.Tests.Infrastructure;

/// <summary>
/// Minimal <see cref="IOAuthTokenProvider"/> test double that hands out a fixed bearer token, so the
/// real GitHub/ADO providers and publishers can be tested without the interactive sign-in flow. The
/// daemon's read/post HTTP paths only ever call <see cref="GetAccessTokenAsync"/>; the sign-in lifecycle
/// members are not exercised by those paths and throw if misused.
/// </summary>
internal sealed class FakeOAuthTokenProvider : IOAuthTokenProvider
{
    private readonly string _token;

    public FakeOAuthTokenProvider(string providerId, string token = "fake-access-token")
    {
        ProviderId = providerId;
        _token = token;
    }

    public string ProviderId { get; }

    /// <summary>Access tokens handed out, in call order — so a test can assert how many times we refreshed.</summary>
    public List<string> IssuedTokens { get; } = [];

    public OAuthStatus Status => new(OAuthSignInState.SignedIn, ProviderId, [], null, null);

    public Task HydrateFromStoreAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task<SignInChallenge> BeginSignInAsync(CancellationToken ct = default) =>
        throw new NotSupportedException("FakeOAuthTokenProvider does not run interactive sign-in.");

    public Task SignOutAsync(CancellationToken ct = default) =>
        throw new NotSupportedException("FakeOAuthTokenProvider does not run sign-out.");

    public Task<OAuthAccessToken> GetAccessTokenAsync(
        IReadOnlyList<string>? scopes = null,
        CancellationToken ct = default)
    {
        IssuedTokens.Add(_token);
        return Task.FromResult(new OAuthAccessToken(_token, DateTimeOffset.UtcNow.AddHours(1)));
    }
}
