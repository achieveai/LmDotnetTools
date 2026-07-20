using AchieveAi.LmDotnetTools.LmCore.Models;

namespace AchieveAi.LmDotnetTools.LmMultiTurn.UsageAccounting;

/// <summary>
///     Receives usage observations from anywhere in a conversation tree — the primary loop, sub-agents,
///     and workflow tasks — for accumulation into the root conversation's aggregate (issue #196). A single
///     root-scoped sink is created by the root conversation and shared with descendants, so their usage is
///     attributed to the same root regardless of nesting depth.
/// </summary>
public interface IUsageSink
{
    /// <summary>
    ///     Records a usage observation for a single provider attempt. Idempotent per
    ///     <see cref="UsageRecord.ProviderAttemptId" /> — cumulative streaming updates and replays for the
    ///     same attempt collapse to one billable record.
    /// </summary>
    void RecordUsage(UsageRecord observation);
}
