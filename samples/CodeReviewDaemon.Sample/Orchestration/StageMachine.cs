using CodeReviewDaemon.Sample.Persistence.Models;

namespace CodeReviewDaemon.Sample.Orchestration;

/// <summary>
/// The deterministic, pure stage machine for one review run. Stages advance in a fixed linear order;
/// <see cref="ReviewStage"/> on a persisted run records the <em>last completed</em> milestone, so
/// resume-from-first-incomplete is simply "run the stages after the persisted one"
/// (<see cref="RemainingStages"/>). No I/O, no state — every method is a pure function of its input,
/// which is what makes the orchestrator's progress deterministic and replayable.
/// </summary>
internal static class StageMachine
{
    private static readonly ReviewStage[] OrderArray =
    [
        ReviewStage.Discovered,
        ReviewStage.ContextReady,
        ReviewStage.Reviewed,
        ReviewStage.Judged,
        ReviewStage.Posted,
    ];

    /// <summary>Stages in execution order. <see cref="ReviewStage.Discovered"/> is the initial milestone.</summary>
    public static IReadOnlyList<ReviewStage> Order => OrderArray;

    /// <summary>The terminal stage; a run whose last-completed stage equals this is finished.</summary>
    public static ReviewStage Terminal => Order[^1];

    /// <summary>The next stage after <paramref name="completed"/>, or <c>null</c> when finished.</summary>
    public static ReviewStage? NextStage(ReviewStage completed)
    {
        var index = Array.IndexOf(OrderArray, completed);
        if (index < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(completed), completed, "Unknown review stage.");
        }

        return index + 1 < OrderArray.Length ? OrderArray[index + 1] : null;
    }

    /// <summary><c>true</c> when <paramref name="completed"/> is the terminal stage.</summary>
    public static bool IsComplete(ReviewStage completed) => completed == Terminal;

    /// <summary>
    /// The stages still to execute given the last completed stage — i.e. everything strictly after
    /// <paramref name="lastCompleted"/> in <see cref="Order"/>. Resuming a crashed run replays exactly
    /// these and no completed stage twice.
    /// </summary>
    public static IReadOnlyList<ReviewStage> RemainingStages(ReviewStage lastCompleted)
    {
        var index = Array.IndexOf(OrderArray, lastCompleted);
        if (index < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(lastCompleted), lastCompleted, "Unknown review stage.");
        }

        return [.. OrderArray.Skip(index + 1)];
    }
}
