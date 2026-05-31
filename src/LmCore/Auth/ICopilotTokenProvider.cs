namespace AchieveAi.LmDotnetTools.LmCore.Auth;

/// <summary>
///     Supplies the GitHub OAuth bearer token used to authenticate against the GitHub Copilot API.
/// </summary>
/// <remarks>
///     The captured Copilot CLI traffic shows the raw GitHub OAuth token (<c>gho_…</c>) sent
///     directly as <c>Authorization: Bearer &lt;token&gt;</c> against the enterprise host — no
///     short-lived token exchange is required. Implementations may read an existing CLI credential,
///     run a device-flow login, or return a token supplied out-of-band.
/// </remarks>
public interface ICopilotTokenProvider
{
    /// <summary>
    ///     Returns a GitHub OAuth bearer token (without the <c>Bearer </c> prefix).
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="InvalidOperationException">No token could be resolved.</exception>
    Task<string> GetTokenAsync(CancellationToken cancellationToken = default);
}
