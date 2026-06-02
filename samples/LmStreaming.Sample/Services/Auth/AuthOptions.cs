namespace LmStreaming.Sample.Services.Auth;

/// <summary>
/// Strongly-typed configuration for the OAuth auth-provider feature.
/// Bound from the <c>Auth</c> configuration section.
/// </summary>
public sealed class AuthOptions
{
    /// <summary>Configuration section name these options are bound from.</summary>
    public const string SectionName = "Auth";

    /// <summary>GitHub device-code sign-in settings.</summary>
    public GitHubAuthOptions Github { get; set; } = new();

    /// <summary>Azure DevOps (Entra) device-code sign-in settings.</summary>
    public AdoAuthOptions Ado { get; set; } = new();

    /// <summary>Gateway↔webhook callback settings.</summary>
    public WebhookOptions Webhook { get; set; } = new();
}

/// <summary>GitHub OAuth/device-code settings.</summary>
public sealed class GitHubAuthOptions
{
    /// <summary>GitHub App (or OAuth App) client id. When null/empty, GitHub auth is disabled.</summary>
    public string? ClientId { get; set; }

    /// <summary>Scopes requested during the GitHub device-code flow.</summary>
    public string[] Scopes { get; set; } = ["repo", "read:org"];
}

/// <summary>Azure DevOps (Entra) OAuth/device-code settings.</summary>
public sealed class AdoAuthOptions
{
    /// <summary>Entra app (public client) id. When null/empty, ADO auth is disabled.</summary>
    public string? ClientId { get; set; }

    /// <summary>Entra tenant; "organizations" works for multi-tenant work/school accounts.</summary>
    public string TenantId { get; set; } = "organizations";

    /// <summary>Azure DevOps resource scope + offline_access for refresh tokens.</summary>
    public string[] Scopes { get; set; } = ["499b84ac-1321-427f-aa17-267ca6975798/.default", "offline_access"];
}

/// <summary>Gateway↔webhook callback settings.</summary>
public sealed class WebhookOptions
{
    /// <summary>Externally-reachable base URL the gateway calls back on (loopback for local dev).</summary>
    public string PublicBaseUrl { get; set; } = "http://127.0.0.1:5000";

    /// <summary>Shared secret the gateway sends as Authorization when calling the webhook. When null, one is generated at startup.</summary>
    public string? GatewaySharedSecret { get; set; }
}
