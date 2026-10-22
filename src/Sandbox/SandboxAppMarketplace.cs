namespace AchieveAi.LmDotnetTools.Sandbox;

/// <summary>
/// One git marketplace this client's app registered with the gateway (ADR 0045), as
/// <see cref="SandboxClient.RegisterAppMarketplaceAsync"/>, <see cref="SandboxClient.ListAppMarketplacesAsync"/>
/// and <see cref="SandboxClient.SyncAppMarketplaceAsync"/> report it. A sandbox mounts it at
/// <c>/marketplaces/&lt;alias&gt;/</c>, at the commit <see cref="ResolvedSha"/> named when the sandbox was created.
/// </summary>
public sealed record SandboxAppMarketplace
{
    /// <summary>The app that owns the registration.</summary>
    public required string AppId { get; init; }

    /// <summary>The marketplace alias: what <see cref="SandboxCreateRequest"/> and plugin refs name.</summary>
    public required string Alias { get; init; }

    /// <summary>The repository URL, as the gateway normalized it.</summary>
    public required string Url { get; init; }

    /// <summary>The gateway's key for the shared clone. Apps registering the same URL share it.</summary>
    public required string RepoKey { get; init; }

    /// <summary>
    /// <c>latest</c> (follows the default branch; a background sync and
    /// <see cref="SandboxClient.SyncAppMarketplaceAsync"/> move it) or the pinned tag, branch or commit as registered.
    /// </summary>
    public required string Ref { get; init; }

    /// <summary>The commit <see cref="Ref"/> resolves to now. New sandboxes mount this commit.</summary>
    public required string ResolvedSha { get; init; }

    /// <summary>
    /// When the alias was first registered, exactly as the gateway reported it (RFC 3339). Kept as text: the
    /// gateway writes nanosecond precision, which <see cref="DateTimeOffset"/> does not parse.
    /// </summary>
    public required string CreatedAt { get; init; }

    /// <summary>When the registration last changed, as text (see <see cref="CreatedAt"/>).</summary>
    public required string UpdatedAt { get; init; }

    /// <summary>
    /// The last failed background sync of a <c>latest</c> registration; <see langword="null"/> after a good one.
    /// The previous commit stays in use while this is set.
    /// </summary>
    public string? LastError { get; init; }
}
