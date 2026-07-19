using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.LmAgentInfra.Auth;

/// <summary>The credential kind of a predefined egress key.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
internal enum PredefinedKeyKind
{
    /// <summary>A list of header name/value pairs injected verbatim (cookie, token, API key, …).</summary>
    CustomHeaders,

    /// <summary>OAuth2 <c>refresh_token</c> grant: the app mints + auto-refreshes an access token.</summary>
    RefreshToken,

    /// <summary>OAuth2 <c>client_credentials</c> grant: the app mints + auto-refreshes an access token.</summary>
    ClientCredentials,
}

/// <summary>One header name/value pair for a <see cref="PredefinedKeyKind.CustomHeaders"/> entry.</summary>
internal sealed record PredefinedHeader(string Name, string Value);

/// <summary>
/// The persisted definition of one predefined egress key. Stored under the gitignored
/// <c>oauth-tokens/</c> directory (SECRET — the header <see cref="Headers"/> values,
/// <see cref="ClientSecret"/>, and <see cref="RefreshToken"/> are credential material and are never
/// logged nor returned by the CRUD API). Kind-specific fields are only meaningful for their kind.
/// </summary>
internal sealed record PredefinedKeyEntry
{
    /// <summary>Stable id; forms the provider id <c>predefined-{Id}</c> and the sandbox rule/provider ids.</summary>
    public required string Id { get; init; }

    /// <summary>The single destination host (exact or <c>*.suffix</c>) this entry authenticates egress to.</summary>
    public required string Host { get; init; }

    /// <summary>The credential kind.</summary>
    public required PredefinedKeyKind Kind { get; init; }

    /// <summary>Header pairs injected verbatim — <see cref="PredefinedKeyKind.CustomHeaders"/> only.</summary>
    public IReadOnlyList<PredefinedHeader> Headers { get; init; } = [];

    /// <summary>Header the minted <c>Bearer</c> token is injected under (OAuth kinds). Defaults to <c>Authorization</c>.</summary>
    public string HeaderName { get; init; } = "Authorization";

    /// <summary>OAuth2 token endpoint URL (OAuth kinds).</summary>
    public string? TokenEndpoint { get; init; }

    /// <summary>OAuth2 client id (OAuth kinds).</summary>
    public string? ClientId { get; init; }

    /// <summary>OAuth2 client secret (client-credentials always; refresh-token when the provider requires it). SECRET.</summary>
    public string? ClientSecret { get; init; }

    /// <summary>OAuth2 refresh token (refresh-token kind). SECRET. Rotated values are persisted in the token store, not here.</summary>
    public string? RefreshToken { get; init; }

    /// <summary>OAuth2 scopes requested when minting (OAuth kinds).</summary>
    public IReadOnlyList<string> Scopes { get; init; } = [];
}
