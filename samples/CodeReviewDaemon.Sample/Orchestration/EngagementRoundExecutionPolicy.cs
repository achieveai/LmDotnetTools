using CodeReviewDaemon.Sample.Configuration;
using CodeReviewDaemon.Sample.Persistence.Models;

namespace CodeReviewDaemon.Sample.Orchestration;

/// <summary>Applies one engagement rollout policy to every admission path.</summary>
internal sealed class EngagementRoundExecutionPolicy
{
    private readonly CodeReviewDaemonOptions _options;
    private readonly EngagementCutoverSeeder _cutoverSeeder;
    private readonly EngagementRoundRunner _runner;

    public EngagementRoundExecutionPolicy(
        CodeReviewDaemonOptions options,
        EngagementCutoverSeeder cutoverSeeder,
        EngagementRoundRunner runner
    )
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _cutoverSeeder = cutoverSeeder ?? throw new ArgumentNullException(nameof(cutoverSeeder));
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
    }

    public async Task RunAsync(EngagementDecision decision, ReviewRun? seed, CancellationToken cancellationToken)
    {
        if (decision.RoundId is not { } roundId)
        {
            return;
        }

        if (_options.EnableEngagementShadowMode)
        {
            return;
        }

        if (
            !_options.EnableEngagementCoordinator
            || !_options.EnableEngagementEligibility
            || !_cutoverSeeder.IsComplete
        )
        {
            return;
        }

        await _runner.RunAsync(decision, seed, cancellationToken).ConfigureAwait(false);
    }
}
