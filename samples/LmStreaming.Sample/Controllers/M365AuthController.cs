using LmStreaming.Sample.Services.Auth;
using Microsoft.AspNetCore.Mvc;

namespace LmStreaming.Sample.Controllers;

/// <summary>
/// UI-facing endpoints that drive the interactive M365 (Microsoft Graph) OAuth sign-in lifecycle
/// (authorization-code + PKCE on a confidential client). Sign-in opens the system browser; the
/// authorization callback lands on this same app's primary port at
/// <see cref="M365AuthOptions.RedirectPath"/> (no standalone HttpListener).
/// </summary>
/// <remarks>
/// SECURITY: these endpoints never surface token material — only the UI-safe
/// <see cref="OAuthStatus"/> record and the <see cref="SignInChallenge"/> (URL that was opened).
/// The callback action returns an inline HTML page with no token information.
/// </remarks>
[ApiController]
[Route("api/auth/m365")]
public sealed class M365AuthController(
    M365OAuthProvider provider,
    ILogger<M365AuthController> logger) : ControllerBase
{
    /// <summary>
    /// Opens the browser to start the interactive sign-in and returns the challenge (the
    /// authorization URL that was opened).
    /// </summary>
    [HttpPost("signin")]
    public async Task<IActionResult> SignIn(CancellationToken ct = default)
    {
        try
        {
            var challenge = await provider.BeginSignInAsync(ct);
            logger.LogInformation("Started OAuth sign-in for provider {ProviderId}.", provider.ProviderId);
            return Ok(challenge);
        }
        catch (InvalidOperationException ex)
        {
            // Provider is registered but not configured (e.g. missing client id / secret).
            logger.LogInformation("OAuth sign-in unavailable for provider {ProviderId}.", provider.ProviderId);
            return Conflict(new { error = ex.Message });
        }
    }

    /// <summary>Returns the current (UI-safe) sign-in status.</summary>
    [HttpGet("status")]
    public IActionResult Status() => Ok(provider.Status);

    /// <summary>Clears stored tokens and resets to the not-started state.</summary>
    [HttpPost("signout")]
    public async Task<IActionResult> SignOut(CancellationToken ct = default)
    {
        await provider.SignOutAsync(ct);
        logger.LogInformation("Signed out OAuth provider {ProviderId}.", provider.ProviderId);
        return NoContent();
    }

    /// <summary>
    /// App-hosted OAuth callback. Entra redirects the browser here with <c>code</c> + <c>state</c>
    /// (or an <c>error</c>); we redeem the code via MSAL and render a small landing page that the
    /// user can close. The leading slash on the route opts out of the class-level <c>api/auth/m365</c>
    /// prefix so the callback path matches the default <see cref="M365AuthOptions.RedirectPath"/>
    /// (which Entra needs configured up-front against its app registration).
    /// </summary>
    [HttpGet("/auth/m365/callback")]
    public async Task<IActionResult> Callback(
        [FromQuery] string? code,
        [FromQuery] string? state,
        [FromQuery] string? error,
        [FromQuery(Name = "error_description")] string? errorDescription,
        CancellationToken ct = default)
    {
        if (!string.IsNullOrEmpty(error))
        {
            // Entra rejected at the authorize step (user denied, consent required, etc.) — surface
            // the OAuth error code in the rendered page so the user can act, but never echo the
            // potentially-verbose description verbatim into HTML (XSS-by-construction guard).
            logger.LogWarning("M365 authorize step failed: {Error}.", error);
            return Content(BuildHtml(success: false, message: $"Sign-in failed: {WebEncode(error)}."), "text/html");
        }

        var failure = await provider.CompleteSignInAsync(code, state, ct);
        var html = failure is null
            ? BuildHtml(success: true, message: "Sign-in complete. You can close this tab.")
            : BuildHtml(success: false, message: $"Sign-in failed: {WebEncode(failure)}.");
        return Content(html, "text/html");
    }

    private static string BuildHtml(bool success, string message)
    {
        var title = success ? "Sign-in complete" : "Sign-in failed";
        return "<!doctype html><html><body style=\"font-family:sans-serif;max-width:480px;margin:40px auto;color:#222\">"
            + $"<h3>{title}</h3><p>{message}</p></body></html>";
    }

    private static string WebEncode(string value) => System.Net.WebUtility.HtmlEncode(value);
}
