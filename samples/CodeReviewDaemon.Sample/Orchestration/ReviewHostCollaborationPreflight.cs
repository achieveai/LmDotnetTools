using CodeReviewDaemon.Sample.Agents;

namespace CodeReviewDaemon.Sample.Orchestration;

/// <summary>
/// Asserts, once at startup, that the LmStreaming review host runs with
/// <c>AgentCollaboration__Enabled=true</c>, and refuses to start the daemon when it does not.
/// <para>
/// This is an <see cref="IHostedService"/> rather than a <see cref="BackgroundService"/> deliberately:
/// throwing from <see cref="StartAsync"/> aborts host startup and surfaces the exception directly, which is
/// the behaviour wanted. A <c>BackgroundService</c> would report the same fault as a mysterious process exit
/// several layers removed from the cause.
/// </para>
/// <para>
/// It exists because the alternative — an export in the relaunch script — is the half that CAN silently
/// regress: a script can be bypassed, run from the wrong directory, or replaced, and the failure it guards
/// against announces itself nowhere. With collaboration off the reviews still complete and the notes are
/// still committed; only every delegate reviewer's transcript is replaced by a placeholder stub. The daemon
/// asserting its own precondition is what makes that non-regressible.
/// </para>
/// </summary>
internal sealed class ReviewHostCollaborationPreflight(
    LmStreamingS2SClient client,
    ILogger<ReviewHostCollaborationPreflight> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await client.EnsureAgentCollaborationAsync(cancellationToken).ConfigureAwait(false);
        logger.LogInformation(
            "Review host preflight: agent collaboration is enabled, so sub-agent transcripts are readable "
                + "and delegate reviewers' notes will carry their reasoning rather than a placeholder.");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
