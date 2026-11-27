namespace AchieveAi.LmDotnetTools.Sandbox;

/// <summary>
/// One git marketplace this client's app registered with the gateway (ADR 0045), as
/// <see cref="SandboxClient.RegisterAppMarketplaceAsync(string, string, string, CancellationToken)"/>, <see cref="SandboxClient.ListAppMarketplacesAsync"/>
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

    /// <summary>The isolated catalog package, or <see langword="null"/> for a whole-marketplace registration.</summary>
    public string? Plugin { get; init; }

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

/// <summary>
/// What <see cref="SandboxClient.RegisterAppMarketplaceAsync(string, SandboxAppMarketplaceRegistration, CancellationToken)"/>
/// sends: a repository URL, an optional ref and, for a private GitHub or Azure DevOps repository, an opaque
/// <see cref="ConnectionId"/>. The connection id is a reference the host's own credential broker understands; it is
/// never a provider token. Provider tokens, refresh tokens and the broker's callback URL stay with the host and the
/// gateway operator's configuration, never in this request.
/// </summary>
public sealed record SandboxAppMarketplaceRegistration
{
    /// <summary>The repository's exact <c>https</c> clone URL. Sent as given: a literal <c>.git</c> suffix is kept.</summary>
    public required string Url { get; init; }

    /// <summary>A tag, branch or commit to pin, or <see langword="null"/> for <c>latest</c>.</summary>
    public string? Ref { get; init; }

    /// <summary>
    /// The host's connection id for a private repository: 1 to 128 characters of <c>[A-Za-z0-9_.-]</c>, or
    /// <see langword="null"/> for a public repository (the field is then omitted).
    /// </summary>
    public string? ConnectionId { get; init; }
}

/// <summary>A signed app's registration of one publisher archive, with the download and staged content digests.</summary>
public sealed record SandboxAppMarketplaceArchive(
    string AppId,
    string Alias,
    string Url,
    string Sha256,
    string ContentSha256,
    string Plugin
);

/// <summary>An exact sandbox source selection prepared for one or more publisher archives.</summary>
public sealed class SandboxArchivePreparation
{
    public string SourceVersion { get; }
    public IReadOnlyList<SandboxPluginRef> EffectivePlugins { get; }
    public IReadOnlyList<SandboxAppMarketplaceArchive> Archives { get; }

    internal SandboxArchivePreparation(
        string sourceVersion,
        IReadOnlyList<SandboxPluginRef> effectivePlugins,
        IReadOnlyList<SandboxAppMarketplaceArchive> archives
    )
    {
        SourceVersion = sourceVersion;
        EffectivePlugins = Array.AsReadOnly(effectivePlugins.ToArray());
        Archives = Array.AsReadOnly(archives.ToArray());
    }
}
