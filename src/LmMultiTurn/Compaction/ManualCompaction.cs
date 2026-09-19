namespace AchieveAi.LmDotnetTools.LmMultiTurn.Compaction;

/// <summary>
///     The contract of an operator-requested compaction (a UI button or an S2S call): it bypasses the thresholds,
///     the cooldown, the minimum-gain gate, the failure backoff and the thread rate limit, and still honours Off
///     mode, the kill switch, a provider-owned session, unsafe loop state and one compaction in flight.
/// </summary>
public static class ManualCompaction
{
    /// <summary>The longest focus kept; longer text is cut to this.</summary>
    public const int MaxFocusChars = 2_000;

    /// <summary>Accepted and waiting for an idle loop to pick it up.</summary>
    public const string StatusQueued = "queued";

    /// <summary>Accepted while a run is active; the run applies it before its next provider call.</summary>
    public const string StatusRunning = "running";

    /// <summary>The trimmed focus cut to <see cref="MaxFocusChars" />, or null when there is none.</summary>
    public static string? NormalizeFocus(string? focus)
    {
        var trimmed = focus?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        if (trimmed.Length <= MaxFocusChars)
        {
            return trimmed;
        }

        var end = char.IsHighSurrogate(trimmed[MaxFocusChars - 1]) ? MaxFocusChars - 1 : MaxFocusChars;
        return trimmed[..end];
    }
}

/// <summary>An agent whose harness can compact its context on request.</summary>
public interface IManualCompactionAgent : IMultiTurnAgent
{
    /// <summary>Records a manual compaction request, or answers why it was refused. Never waits for the compaction.</summary>
    Task<ManualCompactionResult> RequestCompactionAsync(string? focus = null, CancellationToken ct = default);
}

/// <summary>Why a manual compaction request was not accepted.</summary>
public static class ManualCompactionRefusals
{
    /// <summary>The loop's compaction mode is not Compact, the kill switch is on, or it has no store.</summary>
    public const string CompactionOff = "compaction_off";

    /// <summary>The provider owns the session history, so there is nothing the harness may cut.</summary>
    public const string ProviderOwnedSession = "provider_owned_session";

    /// <summary>A request is already queued and has not run yet.</summary>
    public const string AlreadyPending = "already_pending";

    /// <summary>A compaction is running right now.</summary>
    public const string InProgress = "in_progress";

    /// <summary>
    ///     Nothing since the active checkpoint could be covered: fewer than two rows follow it, or (on an idle loop)
    ///     every row that does is the tail the cut rules keep and nothing else blocks.
    /// </summary>
    public const string NothingToCompact = "nothing_to_compact";

    /// <summary>
    ///     On an idle loop, something blocks a cut right now: unsafe state (an owed continuation), an open or deferred
    ///     tool call, or a protected run.
    /// </summary>
    public const string NoSafeBoundary = "no_safe_boundary";
}

/// <summary>The answer to a manual compaction request: a request id and status, or a refusal reason.</summary>
public sealed record ManualCompactionResult
{
    public string? RequestId { get; init; }

    /// <summary><see cref="ManualCompaction.StatusQueued" /> or <see cref="ManualCompaction.StatusRunning" />; null when refused.</summary>
    public string? Status { get; init; }

    /// <summary>One of <see cref="ManualCompactionRefusals" />; null when accepted.</summary>
    public string? RefusalReason { get; init; }

    public bool Accepted => RefusalReason is null;

    public static ManualCompactionResult Refused(string reason) => new() { RefusalReason = reason };
}
