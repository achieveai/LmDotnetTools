namespace AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox;

/// <summary>
/// The <c>SandboxGateway:Network</c> configuration block: named egress rules keyed by rule id.
/// </summary>
/// <remarks>
/// Rule ids are the gateway's own rule identifiers, so naming a MANAGED rule
/// (<c>github</c>, <c>github-egress</c>, <c>ado</c>, <c>m365</c>) replaces it wholesale, and marking
/// that entry <c>Enabled: false</c> removes it. <c>predefined-*</c> ids are reserved for runtime
/// egress keys and are rejected.
/// </remarks>
public sealed class SandboxNetworkOptions
{
    /// <summary>Rules keyed by rule id (case-insensitive).</summary>
    public Dictionary<string, SandboxNetworkRuleOptions> Rules { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// One configured egress rule. Every match dimension is a COMMA-DELIMITED scalar string rather than a
/// JSON array on purpose: ASP.NET configuration merges arrays element-by-element across providers, so
/// an environment/user-secrets override of an array element leaves the other elements in place. A
/// scalar string is replaced atomically, which is the only safe semantic for a security boundary.
/// </summary>
public sealed class SandboxNetworkRuleOptions
{
    /// <summary>When <c>false</c> the entry is a tombstone: the rule is not emitted, and a managed rule of the same id is removed.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary><c>allow</c> or <c>deny</c>.</summary>
    public string Action { get; set; } = "allow";

    /// <summary>Comma-delimited exact hosts or single-leading-<c>*.</c> suffix patterns. Required.</summary>
    public string? Hosts { get; set; }

    /// <summary>Comma-delimited TCP ports (1..65535). Required.</summary>
    public string? Ports { get; set; }

    /// <summary>Comma-delimited HTTP methods, or <c>*</c> for any. Required.</summary>
    public string? Methods { get; set; }

    /// <summary>
    /// Comma-delimited path patterns. The gateway grammar is only <c>*</c> (any), a trailing
    /// <c>/*</c> inclusive prefix, or an exact case-sensitive path. Empty means any path.
    /// </summary>
    public string? Paths { get; set; }

    /// <summary>Key of an entry in <c>SandboxGateway:AuthProviders</c>, un-prefixed. Optional.</summary>
    public string? AuthProvider { get; set; }

    /// <summary>Comma-delimited OAuth scopes the rule requires. Optional.</summary>
    public string? RequiredScopes { get; set; }

    /// <summary>Evaluation priority; the gateway evaluates ASCENDING and takes the first match.</summary>
    public int Priority { get; set; } = 500;
}

/// <summary>
/// One configured egress auth provider. Two kinds are supported:
/// <list type="bullet">
/// <item><c>headers</c> — this app injects the configured static request headers from its own auth
/// webhook. Header values never leave the app on the sandbox-create request.</item>
/// <item><c>webhook</c> — an EXTERNAL callback the gateway invokes directly, with its own
/// <see cref="GatewayAuth"/> secret. The app's per-session secret is never handed to it.</item>
/// </list>
/// Configured providers are emitted on the wire under a <c>cfg-</c> id prefix so they can never
/// collide with a managed (<c>github-auth</c>/<c>ado-auth</c>/<c>m365-auth</c>/<c>predefined-*</c>) id.
/// </summary>
public sealed class SandboxEgressAuthProviderOptions
{
    /// <summary>When <c>false</c> the provider is not emitted and no rule may reference it.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary><c>headers</c> or <c>webhook</c>.</summary>
    public string? Type { get; set; }

    /// <summary>Comma-delimited hosts this provider's credential may be injected toward. Required.</summary>
    public string? Hosts { get; set; }

    /// <summary><c>webhook</c> only: the absolute HTTPS callback URL the gateway invokes.</summary>
    public string? Endpoint { get; set; }

    /// <summary>
    /// <c>webhook</c> only: the gateway↔endpoint shared secret. SECRET — never logged and never
    /// echoed in a validation message.
    /// </summary>
    public string? GatewayAuth { get; set; }

    /// <summary>How long the gateway may cache a resolved credential for this provider.</summary>
    public int CacheTtlSeconds { get; set; } = 300;

    /// <summary>Comma-delimited OAuth scopes. Optional.</summary>
    public string? RequiredScopes { get; set; }

    /// <summary><c>headers</c> only: headers to inject, keyed by header name.</summary>
    public Dictionary<string, SandboxEgressHeaderOptions> Headers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>One injected request header. SECRET — <see cref="Value"/> is never logged.</summary>
public sealed class SandboxEgressHeaderOptions
{
    /// <summary>When <c>false</c> the header is not injected.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The header value injected verbatim by the auth webhook.</summary>
    public string? Value { get; set; }
}
