using LmStreaming.Sample.Services.Auth;
using Microsoft.AspNetCore.Mvc;

namespace LmStreaming.Sample.Controllers;

/// <summary>
/// UI-facing endpoints that drive the interactive (browser + loopback redirect) OAuth sign-in
/// lifecycle for each registered provider (e.g. "github", "ado"). Sign-in opens the system browser;
/// the caller polls <see cref="Status"/> until the background loopback exchange completes.
/// </summary>
/// <remarks>
/// SECURITY: these endpoints never surface token material — only the UI-safe
/// <see cref="OAuthStatus"/> record and the <see cref="SignInChallenge"/> (URL that was opened).
/// </remarks>
[ApiController]
[Route("api/auth")]
public sealed class AuthController(
    IEnumerable<IOAuthTokenProvider> providers,
    ILogger<AuthController> logger) : ControllerBase
{
    /// <summary>
    /// Opens the browser to start the interactive sign-in for the named provider and returns the
    /// challenge (the authorization URL that was opened).
    /// </summary>
    /// <param name="provider">Provider id, e.g. "github" or "ado".</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpPost("{provider}/signin")]
    public async Task<IActionResult> SignIn(string provider, CancellationToken ct = default)
    {
        var resolved = Resolve(provider);
        if (resolved is null)
        {
            return NotFound();
        }

        try
        {
            var challenge = await resolved.BeginSignInAsync(ct);
            logger.LogInformation("Started OAuth sign-in for provider {ProviderId}.", resolved.ProviderId);
            return Ok(challenge);
        }
        catch (InvalidOperationException ex)
        {
            // Provider is registered but not configured (e.g. missing ClientId).
            logger.LogInformation("OAuth sign-in unavailable for provider {ProviderId}.", resolved.ProviderId);
            return Conflict(new { error = ex.Message });
        }
    }

    /// <summary>Returns the current (UI-safe) sign-in status for the named provider.</summary>
    /// <param name="provider">Provider id, e.g. "github" or "ado".</param>
    [HttpGet("{provider}/status")]
    public IActionResult Status(string provider)
    {
        var resolved = Resolve(provider);
        return resolved is null ? NotFound() : Ok(resolved.Status);
    }

    /// <summary>Clears stored tokens and resets the named provider to its not-started state.</summary>
    /// <param name="provider">Provider id, e.g. "github" or "ado".</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpPost("{provider}/signout")]
    public async Task<IActionResult> SignOut(string provider, CancellationToken ct = default)
    {
        var resolved = Resolve(provider);
        if (resolved is null)
        {
            return NotFound();
        }

        await resolved.SignOutAsync(ct);
        logger.LogInformation("Signed out OAuth provider {ProviderId}.", resolved.ProviderId);
        return NoContent();
    }

    /// <summary>Resolves a registered provider by id, matching <see cref="IOAuthTokenProvider.ProviderId"/> case-insensitively.</summary>
    private IOAuthTokenProvider? Resolve(string provider) =>
        providers.FirstOrDefault(p => string.Equals(p.ProviderId, provider, StringComparison.OrdinalIgnoreCase));
}
