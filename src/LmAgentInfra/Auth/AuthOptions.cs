namespace AchieveAi.LmDotnetTools.LmAgentInfra.Auth;

/// <summary>
/// Strongly-typed configuration for the OAuth auth-provider feature.
/// Bound from the <c>Auth</c> configuration section.
/// </summary>
public sealed class AuthOptions
{
    /// <summary>Configuration section name these options are bound from.</summary>
    public const string SectionName = "Auth";

    /// <summary>GitHub interactive (browser/loopback) sign-in settings.</summary>
    public GitHubAuthOptions Github { get; set; } = new();

    /// <summary>Azure DevOps (Entra/MSAL) interactive sign-in settings.</summary>
    public AdoAuthOptions Ado { get; set; } = new();

    /// <summary>Microsoft 365 (Entra/MSAL confidential-client) interactive sign-in settings.</summary>
    public M365AuthOptions M365 { get; set; } = new();

    /// <summary>Gateway↔webhook callback settings.</summary>
    public WebhookOptions Webhook { get; set; } = new();

    /// <summary>
    /// Optional override for the directory that persists OAuth tokens (the <c>github.json</c> /
    /// <c>msal-ado.bin</c> store). When null/empty the store lives under
    /// <c>{AppContext.BaseDirectory}/oauth-tokens</c> (the default). Pointing this at an existing
    /// signed-in store lets a second process (e.g. an E2E test host) reuse the same credentials, and
    /// lets operators relocate the store off the app base directory.
    /// </summary>
    public string? TokenStoreDir { get; set; }
}

/// <summary>GitHub OAuth (authorization-code / loopback web-app flow) settings.</summary>
public sealed class GitHubAuthOptions
{
    /// <summary>GitHub App (or OAuth App) client id. When null/empty, GitHub auth is disabled.</summary>
    public string? ClientId { get; set; }

    /// <summary>
    /// GitHub OAuth client secret. Required: GitHub mandates the secret in the code→token exchange
    /// even with PKCE. When null/empty, GitHub sign-in is disabled. The GitHub CLI's first-party
    /// id/secret (published as safe to embed) are the convenient default supplied via config.
    /// </summary>
    public string? ClientSecret { get; set; }

    /// <summary>
    /// Scopes requested during the GitHub authorization-code flow. Single source of truth is
    /// configuration (<c>appsettings.Development.json</c> / env); leaving the default empty avoids
    /// the .NET config-binder's append-onto-pre-populated-array gotcha that would otherwise emit
    /// duplicate scopes in <c>OAuthStatus.Scopes</c>.
    /// </summary>
    public string[] Scopes { get; set; } = [];
}

/// <summary>Azure DevOps (Entra/MSAL) interactive OAuth settings.</summary>
public sealed class AdoAuthOptions
{
    /// <summary>Entra app (public client) id. When null/empty, ADO auth is disabled.</summary>
    public string? ClientId { get; set; }

    /// <summary>Entra tenant; "organizations" works for multi-tenant work/school accounts.</summary>
    public string TenantId { get; set; } = "organizations";

    /// <summary>
    /// Azure DevOps resource scope. Single source of truth is configuration; default is empty so
    /// the .NET config-binder does not append onto a pre-populated array (which would surface as
    /// duplicate scopes in <c>OAuthStatus.Scopes</c>). <c>offline_access</c> may be supplied in
    /// config for parity with other tools; the provider strips reserved scopes before calling MSAL.
    /// </summary>
    public string[] Scopes { get; set; } = [];
}

/// <summary>
/// Microsoft 365 (Entra) OAuth settings — confidential web client with auth-code + PKCE.
/// The client secret is REQUIRED and must NEVER be committed; supply it via user-secrets / env
/// (e.g. <c>Auth__M365__ClientSecret</c>).
/// </summary>
public sealed class M365AuthOptions
{
    /// <summary>Entra app (confidential client) id. When null/empty, M365 auth is disabled.</summary>
    public string? ClientId { get; set; }

    /// <summary>
    /// Entra app client secret. REQUIRED to enable M365 sign-in; supply via user-secrets / env only,
    /// NEVER in committed appsettings. When null/empty, M365 sign-in is disabled.
    /// </summary>
    public string? ClientSecret { get; set; }

    /// <summary>
    /// Entra tenant id (or "common"/"organizations"). The configured app registration is
    /// multi-tenant; tenant-pin by default so token caching is per-tenant.
    /// </summary>
    public string TenantId { get; set; } = "common";

    /// <summary>
    /// Delegated Microsoft Graph scopes requested during the authorization-code flow. Single
    /// source of truth is configuration; default is empty so the .NET config-binder does not
    /// append onto a pre-populated array (which would surface as duplicate scopes in
    /// <c>OAuthStatus.Scopes</c>). MSAL injects reserved OIDC scopes (<c>openid</c>,
    /// <c>profile</c>, <c>offline_access</c>) itself.
    /// </summary>
    public string[] Scopes { get; set; } = [];

    /// <summary>
    /// App-hosted callback path on the primary port (e.g. <c>http://localhost:5000/auth/m365/callback</c>).
    /// Must match the redirect URI registered in the Entra app. Leading slash required.
    /// </summary>
    public string RedirectPath { get; set; } = "/auth/m365/callback";
}

/// <summary>Gateway↔webhook callback settings.</summary>
public sealed class WebhookOptions
{
    /// <summary>Externally-reachable base URL the gateway calls back on (loopback for local dev).</summary>
    public string PublicBaseUrl { get; set; } = "http://127.0.0.1:5000";

    /// <summary>
    /// Seconds a not-signed-in webhook call is HELD open awaiting an interactive sign-in (deferred
    /// auth) before resolving deny. While held, connected chat clients receive an
    /// <c>auth_required</c> WebSocket frame prompting the user to sign in; the moment a valid token
    /// appears the held call resolves allow. Set to 0 (or negative) to disable deferral and restore
    /// the legacy immediate-deny behavior. Keep this below the gateway's own webhook client timeout
    /// or the gateway gives up before the user can sign in.
    /// </summary>
    public int HoldTimeoutSeconds { get; set; } = 120;

    /// <summary>
    /// Interval in seconds between token-acquisition attempts while a webhook call is held.
    /// </summary>
    public double PollIntervalSeconds { get; set; } = 1.0;

    /// <summary>
    /// Webhook public base URL with the trailing slash stripped — the canonical form callers
    /// concatenate route segments to. Centralised so the gateway → app callback URLs all agree
    /// even if a future operator misconfigures <see cref="PublicBaseUrl"/> with a trailing slash.
    /// </summary>
    public string CallbackBaseUrl => PublicBaseUrl.TrimEnd('/');
}
