using System.Net;
using AchieveAi.LmDotnetTools.LmAgentInfra.Auth;
using Microsoft.AspNetCore.Mvc;

namespace LmStreaming.Sample.Controllers;

/// <summary>
/// Browser entry point that lets an operator visit <c>/auth/{provider}</c> directly and either see
/// the already-signed-in status or start an interactive sign-in (which opens the system browser to
/// the provider's authorize URL). The page that's served then polls
/// <c>GET /api/auth/{provider}/status</c> until the sign-in resolves — there is no WebSocket /
/// SignalR coupling, just a tiny inline script.
/// </summary>
/// <remarks>
/// SECURITY: this endpoint never surfaces token material. It only renders the provider id, the
/// current <see cref="OAuthSignInState"/>, and (for failures) an HTML-encoded OAuth error code.
/// Unknown provider ids return 404 — defense-in-depth so a typo can't be used to enumerate
/// internal state.
/// </remarks>
[ApiController]
public sealed class AuthPagesController(
    IEnumerable<IOAuthTokenProvider> providers,
    ILogger<AuthPagesController> logger) : ControllerBase
{
    /// <summary>
    /// GET landing page for a provider. If already signed in, renders a success page; otherwise
    /// kicks off <see cref="IOAuthTokenProvider.BeginSignInAsync"/> (opens the browser) and returns
    /// a small HTML page that polls the status endpoint until sign-in completes or fails.
    /// </summary>
    [HttpGet("auth/{providerId}")]
    public async Task<IActionResult> Page(string providerId, CancellationToken ct = default)
    {
        var provider = providers.FirstOrDefault(p => string.Equals(p.ProviderId, providerId, StringComparison.OrdinalIgnoreCase));
        if (provider is null)
        {
            // 404 (not 400) to avoid leaking which provider ids are valid via differential responses.
            return NotFound();
        }

        var status = provider.Status;
        if (status.State == OAuthSignInState.SignedIn)
        {
            return Content(BuildSignedInHtml(provider.ProviderId, status), "text/html");
        }

        try
        {
            _ = await provider.BeginSignInAsync(ct);
        }
        catch (InvalidOperationException ex)
        {
            // Provider is registered but not configured (e.g. missing ClientId/secret). Surface the
            // unconfigured error inline rather than a 409 so the user sees what to fix.
            logger.LogInformation("OAuth sign-in unavailable for provider {ProviderId}.", provider.ProviderId);
            return Content(BuildUnavailableHtml(provider.ProviderId, ex.Message), "text/html");
        }

        return Content(BuildPendingHtml(provider.ProviderId), "text/html");
    }

    private static string BuildSignedInHtml(string providerId, OAuthStatus status)
    {
        var encoded = Encode(providerId);
        var accountLine = string.IsNullOrEmpty(status.Account)
            ? string.Empty
            : $"<p>Account: <code>{Encode(status.Account)}</code></p>";

        // Scopes the user granted, surfaced verbatim from the provider's last sign-in so the
        // operator can confirm the grant covers what the app needs. Empty list (e.g. provider has
        // not exposed scopes) just renders nothing.
        var scopesLine = status.Scopes is { Count: > 0 }
            ? $"<p>Scopes: <code>{Encode(string.Join(' ', status.Scopes))}</code></p>"
            : string.Empty;

        // ExpiresAtUtc is rendered as an ISO-8601 absolute timestamp; the operator can compare it
        // against now without us guessing a locale. Some providers (M365 hydrate) don't surface an
        // expiry — omit the line in that case.
        var expiryLine = status.ExpiresAtUtc is { } expiresAt
            ? $"<p>Expires: <code>{Encode(expiresAt.UtcDateTime.ToString("o", System.Globalization.CultureInfo.InvariantCulture))}</code></p>"
            : string.Empty;

        return Page(
            rawTitle: $"Signed in to {providerId}",
            body: $"<h3>Signed in to {encoded}</h3>{accountLine}{scopesLine}{expiryLine}<p>You can close this tab.</p>");
    }

    private static string BuildUnavailableHtml(string providerId, string error)
    {
        var encoded = Encode(providerId);
        return Page(
            rawTitle: $"{providerId} unavailable",
            body: $"<h3>{encoded} sign-in unavailable</h3><p>{Encode(error)}</p>");
    }

    private static string BuildPendingHtml(string providerId)
    {
        var encoded = Encode(providerId);
        // Inline poll script — checks /api/auth/{providerId}/status every 1.5s and updates the page
        // when the state changes. Self-contained (no external script tag, no token material).
        // providerId is embedded via System.Text.Json.JsonSerializer.Serialize so any unusual
        // character is escaped as a JSON string literal (not interpolated as raw JS).
        var script = "<script>"
            + "const PROVIDER=" + System.Text.Json.JsonSerializer.Serialize(providerId) + ";"
            + "async function poll(){try{const r=await fetch('/api/auth/'+encodeURIComponent(PROVIDER)+'/status');if(!r.ok)return;"
            + "const s=await r.json();const el=document.getElementById('state');if(!el)return;"
            + "el.textContent=s.state;"
            + "if(s.state==='SignedIn'){document.getElementById('msg').textContent='Signed in. You can close this tab.';return;}"
            + "if(s.state==='Failed'){document.getElementById('msg').textContent='Sign-in failed: '+(s.error||'unknown');return;}"
            + "setTimeout(poll,1500);}catch(e){setTimeout(poll,3000);}}poll();"
            + "</script>";
        return Page(
            rawTitle: $"Signing in to {providerId}",
            body: $"<h3>Signing in to {encoded}</h3>"
                + $"<p>Status: <code id=\"state\">Pending</code></p>"
                + $"<p id=\"msg\">A browser window has been opened to complete sign-in. This page will update automatically.</p>"
                + script);
    }

    // rawTitle is raw text (HTML-encoded here exactly once); body is already-rendered HTML and
    // passes through unchanged.
    private static string Page(string rawTitle, string body) =>
        "<!doctype html><html><head><meta charset=\"utf-8\"><title>" + Encode(rawTitle) + "</title></head>"
        + "<body style=\"font-family:sans-serif;max-width:520px;margin:40px auto;color:#222\">"
        + body + "</body></html>";

    private static string Encode(string value) => WebUtility.HtmlEncode(value ?? string.Empty);
}
