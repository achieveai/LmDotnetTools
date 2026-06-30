using AchieveAi.LmDotnetTools.LmAgentInfra.Auth;
using Microsoft.AspNetCore.Mvc;

namespace LmStreaming.Sample.Controllers;

/// <summary>
/// UI-facing endpoints that drive the interactive (browser + loopback) Azure DevOps OAuth sign-in
/// lifecycle. Sign-in opens the system browser; the caller polls <see cref="Status"/> until the
/// background loopback exchange completes.
/// </summary>
/// <remarks>
/// SECURITY: these endpoints never surface token material — only the UI-safe
/// <see cref="OAuthStatus"/> record and the <see cref="SignInChallenge"/> (URL that was opened).
/// </remarks>
[ApiController]
[Route("api/auth/ado")]
public sealed class AdoAuthController(
    AdoOAuthProvider provider,
    ILogger<AdoAuthController> logger) : ControllerBase
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
}
