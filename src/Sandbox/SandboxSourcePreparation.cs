using System.Text.Json;

namespace AchieveAi.LmDotnetTools.Sandbox;

/// <summary>
/// The source selection <see cref="SandboxClient.PrepareSourcesAsync"/> prepares. Send the SAME
/// <see cref="Marketplaces"/>, <see cref="ExplicitMarketplaces"/> and <see cref="PluginSelection"/> to
/// <see cref="SandboxClient.CreateAsync"/> with the prepared <see cref="SandboxSourcePreparation.SourceVersion"/> as
/// <see cref="SandboxCreateRequest.ExpectedSourceVersion"/>. No workspace, environment or credential is sent here.
/// </summary>
public sealed class SandboxSourcePreparationRequest
{
    /// <summary>
    /// Marketplace aliases, defensively copied. As on <see cref="SandboxCreateRequest.Marketplaces"/>: empty omits the
    /// field (the gateway default set) unless <see cref="ExplicitMarketplaces"/> is set, which sends <c>[]</c>.
    /// </summary>
    public IReadOnlyList<string> Marketplaces { get; }

    /// <summary>Sends <see cref="Marketplaces"/> exactly, an empty list as an explicit <c>[]</c>.</summary>
    public bool ExplicitMarketplaces { get; init; }

    /// <summary>Tri-state plugin selection, as on <see cref="SandboxCreateRequest.PluginSelection"/>.</summary>
    public IReadOnlyList<SandboxPluginRef>? PluginSelection { get; }

    /// <summary>Fetch the selected Git sources before capturing them. Consumed here; never sent to create.</summary>
    public bool Refresh { get; init; }

    /// <summary>Refuse a capture that is not fresh. Send the same value on <see cref="SandboxCreateRequest.RequireFresh"/>.</summary>
    public bool RequireFresh { get; init; }

    /// <summary>
    /// The longest <see cref="SandboxClient.PrepareSourcesAsync"/> waits for a <c>preparing</c> operation, submission
    /// and polling together. Default 5 minutes. Each HTTP call is also bounded by the transport timeout.
    /// </summary>
    public TimeSpan MaxWait { get; init; } = TimeSpan.FromMinutes(5);

    public SandboxSourcePreparationRequest(
        IReadOnlyList<string>? marketplaces = null,
        IReadOnlyList<SandboxPluginRef>? pluginSelection = null
    )
    {
        Marketplaces = marketplaces is null ? [] : [.. marketplaces];
        PluginSelection = pluginSelection is null ? null : [.. pluginSelection];
    }
}

/// <summary>The terminal outcome of a source preparation that did not fail.</summary>
public enum SandboxSourcePreparationStatus
{
    /// <summary>Every source is an exact Git commit; <see cref="SandboxSourcePreparation.SourceVersion"/> is set.</summary>
    Ready,

    /// <summary>
    /// Some source is mutable (an operator filesystem marketplace), so there is no version to guard a create with
    /// (<see cref="SandboxSourcePreparation.ErrorCode"/> is <c>source_snapshot_unsupported</c>).
    /// </summary>
    Unversioned,
}

/// <summary>
/// What <see cref="SandboxClient.PrepareSourcesAsync"/> captured. A failed preparation is never returned: it throws
/// <see cref="SandboxException"/> with <see cref="SandboxException.ErrorCode"/> set.
/// </summary>
public sealed class SandboxSourcePreparation
{
    public required SandboxSourcePreparationStatus Status { get; init; }

    /// <summary>The last operation id the gateway named, or <see langword="null"/> when it answered at once.</summary>
    public string? OperationId { get; init; }

    /// <summary>The version to send as <see cref="SandboxCreateRequest.ExpectedSourceVersion"/>; null when unversioned.</summary>
    public string? SourceVersion { get; init; }

    /// <summary>The gateway's <c>error_code</c> on a non-failed reply (<c>source_snapshot_unsupported</c> when unversioned).</summary>
    public string? ErrorCode { get; init; }

    /// <summary>Each captured catalog or plugin root, with the commit it resolved to.</summary>
    public required IReadOnlyList<SandboxSourceReceiptEntry> Sources { get; init; }

    /// <summary>The whole <c>sourceReceipt</c> object as the gateway sent it, for fields this SDK does not model.</summary>
    public required JsonElement RawSourceReceipt { get; init; }
}

/// <summary>A marketplace's resolved Git catalog commit, as a sandbox create or GET reports it.</summary>
public sealed record SandboxResolvedMarketplaceRevision(string Marketplace, string Commit);

/// <summary>One source of a preparation receipt. Git fields are null for a mutable or archive source.</summary>
public sealed record SandboxSourceReceiptEntry(
    string Kind,
    string Marketplace,
    string? Plugin,
    string? Url,
    string? RepoKey,
    string? Subdir,
    string? Sha
);
