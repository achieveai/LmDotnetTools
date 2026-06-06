namespace LmStreaming.Sample.Services.Auth;

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

    /// <summary>Scopes requested during the GitHub authorization-code flow.</summary>
    public string[] Scopes { get; set; } = ["repo", "read:org"];
}

/// <summary>Azure DevOps (Entra/MSAL) interactive OAuth settings.</summary>
public sealed class AdoAuthOptions
{
    /// <summary>Entra app (public client) id. When null/empty, ADO auth is disabled.</summary>
    public string? ClientId { get; set; }

    /// <summary>Entra tenant; "organizations" works for multi-tenant work/school accounts.</summary>
    public string TenantId { get; set; } = "organizations";

    /// <summary>
    /// Azure DevOps resource scope. <c>offline_access</c> may be present for parity with other tools;
    /// MSAL manages refresh itself and the provider strips reserved scopes before calling MSAL.
    /// </summary>
    public string[] Scopes { get; set; } = ["499b84ac-1321-427f-aa17-267ca6975798/.default", "offline_access"];
}

/// <summary>Gateway↔webhook callback settings.</summary>
public sealed class WebhookOptions
{
    /// <summary>Externally-reachable base URL the gateway calls back on (loopback for local dev).</summary>
    public string PublicBaseUrl { get; set; } = "http://127.0.0.1:5000";

    /// <summary>Shared secret the gateway sends as Authorization when calling the webhook. When null, one is generated at startup.</summary>
    public string? GatewaySharedSecret { get; set; }

    /// <summary>
    /// Webhook public base URL with the trailing slash stripped — the canonical form callers
    /// concatenate route segments to. Centralised so the gateway → app callback URLs all agree
    /// even if a future operator misconfigures <see cref="PublicBaseUrl"/> with a trailing slash.
    /// </summary>
    public string CallbackBaseUrl => PublicBaseUrl.TrimEnd('/');
}
