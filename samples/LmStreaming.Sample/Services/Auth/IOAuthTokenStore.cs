namespace LmStreaming.Sample.Services.Auth;

/// <summary>Persisted token material for one provider. SECRET — never log RefreshToken/AccessToken.</summary>
public sealed record OAuthTokenRecord(
    string Provider,
    string? Account,
    string RefreshToken,
    string? AccessToken,
    DateTimeOffset? AccessTokenExpiresAtUtc,
    IReadOnlyList<string> Scopes);

/// <summary>File-backed store for OAuth refresh/access tokens, keyed by provider id.</summary>
public interface IOAuthTokenStore
{
    Task<OAuthTokenRecord?> GetAsync(string provider, CancellationToken ct = default);

    Task SaveAsync(OAuthTokenRecord record, CancellationToken ct = default);

    Task RemoveAsync(string provider, CancellationToken ct = default);
}
