using System.Net.Http.Headers;
using System.Text.Json;

namespace AchieveAi.LmDotnetTools.LmAgentInfra.Auth;

/// <summary>Parsed OAuth2 token-endpoint response — a success token set or an OAuth <c>error</c>.</summary>
/// <param name="AccessToken">The <c>access_token</c>, or null on error / absent.</param>
/// <param name="RefreshToken">A rotated <c>refresh_token</c>, or null when the endpoint did not return one.</param>
/// <param name="ExpiresIn">The <c>expires_in</c> lifetime in seconds, or 0 when absent.</param>
/// <param name="Error">The OAuth <c>error</c> code (or a synthetic transport error), or null on success.</param>
internal sealed record OAuthTokenEndpointResponse(string? AccessToken, string? RefreshToken, int ExpiresIn, string? Error);

/// <summary>
/// Minimal OAuth2 token-endpoint client: form-POSTs a grant to a configurable token endpoint
/// (<c>Accept: application/json</c>) and parses the standard <c>access_token</c>/<c>refresh_token</c>/
/// <c>expires_in</c>/<c>error</c> response. Modeled on <c>GitHubOAuthProvider.PostTokenAsync</c> but
/// standalone and endpoint-agnostic so <c>PredefinedKeyProvider</c> can drive the
/// <c>refresh_token</c> and <c>client_credentials</c> grants against an arbitrary provider, and so it
/// is unit-testable against a mock <see cref="HttpMessageHandler"/>.
/// </summary>
/// <remarks>SECURITY: never logs the form (it carries the client secret / refresh token) or the response body.</remarks>
internal sealed class OAuthTokenEndpointClient(HttpClient http)
{
    private readonly HttpClient _http = http ?? throw new ArgumentNullException(nameof(http));

    /// <summary>
    /// POSTs <paramref name="form"/> to <paramref name="tokenEndpoint"/> and parses the token/error
    /// response. Never throws for a non-success status or an unparseable body — those surface as an
    /// <see cref="OAuthTokenEndpointResponse.Error"/> so the caller decides whether to deny/invalidate.
    /// </summary>
    public async Task<OAuthTokenEndpointResponse> PostAsync(
        string tokenEndpoint,
        IReadOnlyDictionary<string, string> form,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenEndpoint);
        ArgumentNullException.ThrowIfNull(form);

        using var request = new HttpRequestMessage(HttpMethod.Post, tokenEndpoint)
        {
            Content = new FormUrlEncodedContent(form),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(body))
        {
            return new OAuthTokenEndpointResponse(null, null, 0, "empty_response");
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            return new OAuthTokenEndpointResponse(
                root.TryGetProperty("access_token", out var at) ? at.GetString() : null,
                root.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null,
                root.TryGetProperty("expires_in", out var ei) && ei.TryGetInt32(out var seconds) ? seconds : 0,
                root.TryGetProperty("error", out var er) ? er.GetString() : null);
        }
        catch (JsonException)
        {
            // Non-JSON body (HTML error page, etc.). Surface as a token-free error; never log the body.
            return new OAuthTokenEndpointResponse(null, null, 0, "unparseable_response");
        }
    }
}
