using CodeReviewDaemon.Sample.Persistence.Models;

namespace CodeReviewDaemon.Sample.Orchestration;

/// <summary>
/// One repository/query the poller watches, with the review parameters to stamp onto runs it
/// discovers. The default <see cref="Mode"/> is <c>collect-only</c> — the daemon never posts unless a
/// run is explicitly created in <c>post</c> mode (the safety default; feature-flag wiring is P2.3).
/// </summary>
internal sealed record PrPollTarget
{
    public required string Provider { get; init; }

    public required RepoIdentity Repo { get; init; }

    /// <summary>Repo/query identity the cursor advances (e.g. <c>owner/repo:open-prs</c>).</summary>
    public required string Scope { get; init; }

    public string Mode { get; init; } = "collect-only";

    public string ReviewKind { get; init; } = "full";

    public string VariantId { get; init; } = "primary";

    /// <summary>Model id stamped onto runs discovered for this target (the primary review's model).</summary>
    public string? ModelId { get; init; }
}
