extern alias Reviewd;

using AchieveAi.LmDotnetTools.Sandbox;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using Microsoft.Data.Sqlite;
using Reviewd::Reviewd.Sample;

namespace CodeReviewDaemon.Sample.Tests.Scenarios;

/// <summary>
///     A run that loses its slot claim to a sweep's reset: what it may still write, and what it must leave as the reset
///     recorded it. Each run is a real <c>ReviewRunner.RunAsync</c> on <see cref="ScriptedReviewRun"/>.
/// </summary>
public sealed class ReviewdClaimLossTests
{
    // A reset that releases the claim right after the run's claim check must not be overwritten by the save that
    // follows it: at a checkpoint, or in the failure path's retained save after a cancel or a failed step. The check and
    // the save are one transaction, so the reset here lands inside it (right after the renewal), and the run stops with
    // the attempt as the reset left it.
    [Theory]
    [InlineData("checkpoint")]
    [InlineData("canceled")]
    [InlineData("step-failed")]
    public async Task A_reset_between_the_claim_check_and_the_save_is_never_overwritten(string path)
    {
        await using var run = await ScriptedReviewRun.CreateAsync();
        run.OnPhase = async (phase, _, _, token) =>
        {
            if (phase != "verify")
            {
                return null;
            }
            await run.ResetAfterNextRenewalAsync();
            if (path == "canceled")
            {
                await run.Cancellation.CancelAsync();
                throw new OperationCanceledException(token);
            }
            if (path == "step-failed")
            {
                throw new SandboxException(SandboxErrorKind.TransportTimeout, "verify transport timeout (00:00:30).");
            }
            return null;
        };

        await FluentActions.Invoking(run.RunAsync).Should().ThrowAsync<ReviewRunner.SlotClaimLostException>();

        var attempt = await run.ReadAsync(run.AttemptKey);
        attempt["stage"]!.GetValue<string>().Should().Be("failed-released", "the reset's record stands");
        attempt["cleanupState"]!.GetValue<string>().Should().Be("released", "owned over released would pin the lane");
        (await run.ReadAsync(run.OwnerKey))["state"]!.GetValue<string>().Should().Be("released");
        run.Phases.Should().NotContain("commit");
    }

    // The commit lands after a reset took the claim, and its first save after that is canceled or fails once. The
    // failure path keeps the commit's result, released, since the reset already cleaned the slot: owned, it would
    // pin the lane; dropped, the archive that landed would be forgotten.
    [Theory]
    [InlineData("canceled")]
    [InlineData("write-failed")]
    public async Task A_landed_commits_first_save_after_its_claim_was_lost_stays_released_through_a_cancel_or_a_failed_write(
        string path
    )
    {
        await using var run = await ScriptedReviewRun.CreateAsync();
        run.OnPhase = async (phase, args, _, token) =>
        {
            if (phase != "commit")
            {
                return null;
            }
            await run.ResetAsync(args, token);
            if (path == "canceled")
            {
                await run.Cancellation.CancelAsync();
            }
            else
            {
                await run.FailNextAttemptWriteAsync("commit-completed");
            }
            return ScriptedReviewRun.Committed();
        };

        var failure = await FluentActions.Invoking(run.RunAsync).Should().ThrowAsync<Exception>();

        if (path == "canceled")
        {
            failure.Which.Should().BeAssignableTo<OperationCanceledException>();
        }
        else
        {
            failure.Which.Should().BeOfType<SqliteException>();
        }
        var attempt = await run.ReadAsync(run.AttemptKey);
        attempt["stage"]!.GetValue<string>().Should().Be("commit-completed", "the commit landed");
        attempt["cleanupState"]!.GetValue<string>().Should().Be("released");
        attempt["artifactCommit"]!["commit"]!.GetValue<string>().Should().Be(new string('c', 40));
        (await run.ReadAsync(run.OwnerKey))["state"]!.GetValue<string>().Should().Be("released");
        run.Phases.Should().NotContain("cleanup", "the reset already cleaned the slot");
    }

    // Prepare refused the slot (not idle, or the gateway at capacity) and releasing this run's own claim then fails.
    // The run ends with prepare's own error, not the release's, and the claim stays owned on its token with the
    // release error saved on the attempt for an operator.
    [Theory]
    [InlineData("not-idle")]
    [InlineData("capacity")]
    public async Task A_refused_prepare_whose_release_fails_ends_with_its_own_error_and_keeps_its_claim(string refusal)
    {
        await using var run = await ScriptedReviewRun.CreateAsync();
        Exception refused =
            refusal == "not-idle"
                ? new ReviewRunner.SlotNotIdleException("Native slot retains an owner.")
                : new SandboxException(SandboxErrorKind.Unavailable, "refused", 503)
                {
                    ErrorCode = "operation_capacity_exhausted",
                };
        run.OnPhase = async (phase, _, _, _) =>
        {
            if (phase == "prepare")
            {
                await run.ExecuteAsync(
                    $"""
                    CREATE TRIGGER fail_release BEFORE UPDATE ON cache
                    WHEN NEW.key = '{run.OwnerKey}' AND json_extract(NEW.value, '$.state') = 'released'
                    BEGIN SELECT RAISE(ABORT, 'release write failed'); END;
                    """
                );
                throw refused;
            }
            return null;
        };

        var failure = await FluentActions.Invoking(run.RunAsync).Should().ThrowAsync<Exception>();

        failure.Which.Should().BeSameAs(refused, "prepare's own error, not the release's");
        var attempt = await run.ReadAsync(run.AttemptKey);
        attempt["cleanupError"]!.GetValue<string>().Should().Contain("release write failed");
        attempt["stage"]!.GetValue<string>().Should().Be("prepare-pending");
        var owner = await run.ReadAsync(run.OwnerKey);
        owner["state"]!.GetValue<string>().Should().Be("owned");
        owner["claimToken"]!.GetValue<string>().Should().Be(attempt["claimToken"]!.GetValue<string>());
    }
}
