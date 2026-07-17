namespace AchieveAi.LmDotnetTools.Sandbox;

/// <summary>
/// A webhook-backed OAuth provider to attach to a sandbox at creation, so the sandbox can obtain a
/// caller's OAuth token by calling back out to the app. The SDK only carries this value through to
/// the gateway's <c>auth_providers</c> field — it neither selects providers nor constructs OAuth
/// policy; that decision belongs to the application (e.g. <c>LmAgentInfra</c>).
/// </summary>
public sealed class SandboxAuthProvider
{
    /// <summary>Provider id the gateway/network rules reference (e.g. <c>"github-auth"</c>).</summary>
    public string Id { get; }

    /// <summary>Provider mechanism; currently always <c>"webhook"</c> on the gateway side.</summary>
    public string Type { get; }

    /// <summary>App callback URL the gateway invokes to resolve a token for this provider.</summary>
    public string Endpoint { get; }

    /// <summary>
    /// Gateway↔webhook shared secret the gateway presents when calling <see cref="Endpoint"/>.
    /// SECRET — never logged, never included in an exception message, and never rendered by
    /// <see cref="ToString"/>.
    /// </summary>
    public string GatewayAuth { get; }

    /// <summary>How long the gateway may cache a resolved token for this provider.</summary>
    public int CacheTtlSeconds { get; }

    /// <summary>OAuth scopes this provider requires, defensively copied at construction.</summary>
    public IReadOnlyList<string> RequiredScopes { get; }

    public SandboxAuthProvider(
        string id,
        string type,
        string endpoint,
        string gatewayAuth,
        int cacheTtlSeconds,
        IReadOnlyList<string>? requiredScopes = null
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(gatewayAuth);
        if (cacheTtlSeconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(cacheTtlSeconds), cacheTtlSeconds, "Cache TTL must not be negative.");
        }

        Id = id;
        Type = type;
        Endpoint = endpoint;
        GatewayAuth = gatewayAuth;
        CacheTtlSeconds = cacheTtlSeconds;
        // Defensive copy: the caller's list must not be mutable after construction (e.g. a caller
        // reusing/mutating a shared list instance between calls must never retroactively change an
        // already-built request).
        RequiredScopes = requiredScopes is null ? [] : [.. requiredScopes];
    }

    /// <summary>Redacted rendering — never prints <see cref="GatewayAuth"/>.</summary>
    public override string ToString() =>
        $"SandboxAuthProvider {{ Id = {Id}, Type = {Type}, Endpoint = {Endpoint}, "
            + $"GatewayAuth = [REDACTED], CacheTtlSeconds = {CacheTtlSeconds} }}";
}
