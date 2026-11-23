extern alias Reviewd;

using System.Text.Json.Nodes;
using AchieveAi.LmDotnetTools.Sandbox;
using CodeReviewDaemon.Sample.Agents;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using Microsoft.Data.Sqlite;
using Reviewd::Reviewd.Sample;

namespace CodeReviewDaemon.Sample.Tests.Scenarios;

/// <summary>
///     A run that loses its slot claim to a sweep's reset: what it may still write, and what it must leave as the reset
///     recorded it. Each run is a real <c>ReviewRunner.RunAsync</c> on <see cref="ScriptedReviewRun"/>.
/// </summary>
// Some runs act on a line the run logs (ScriptedReviewRun.ExecuteWhenLogged), and stderr is process-wide.
[Collection(ConsoleCapturingCollection.Name)]
public sealed class ReviewdClaimLossTests
{
    // A reset that releases the claim must not be overwritten by the run's next save: at a checkpoint, or in the failure
    // path's retained save after a cancel or a failed step. A checkpoint's claim check, renewal and save are one
    // transaction, so the reset there lands inside it (right after the renewal); the failure path's save does not renew,
    // so the reset there lands just before it. Either way the run stops with the attempt as the reset left it.
    [Theory]
    [InlineData("checkpoint")]
    [InlineData("canceled")]
    [InlineData("step-failed")]
    public async Task A_reset_between_the_claim_check_and_the_save_is_never_overwritten(string path)
    {
        await using var run = await ScriptedReviewRun.CreateAsync();
        if (path != "checkpoint")
        {
            // The failure path's save does not renew, so the reset lands as the failure path logs, before that save.
            run.ExecuteWhenLogged(
                line => line.Contains(" at verify-pending: ", StringComparison.Ordinal),
                run.ResetSql
            );
        }
        run.OnPhase = async (phase, _, _, token) =>
        {
            if (phase != "verify")
            {
                return null;
            }
            if (path == "checkpoint")
            {
                await run.ResetAfterNextRenewalAsync();
            }
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

        await FluentActions.Invoking(() => run.RunAsync()).Should().ThrowAsync<ReviewRunner.SlotClaimLostException>();

        var attempt = await run.ReadAsync(run.AttemptKey);
        attempt["stage"]!.GetValue<string>().Should().Be("failed-released", "the reset's record stands");
        attempt["cleanupState"]!.GetValue<string>().Should().Be("released", "owned over released would pin the lane");
        (await run.ReadAsync(run.OwnerKey))["state"]!.GetValue<string>().Should().Be("released");
        run.Phases.Should().NotContain("commit");
    }

    // A checkpoint before the commit fails to write (a transient database error), and a sweep resets the slot before the
    // failure path's retained save. That save must find the claim gone and leave the reset's record: the failed write
    // rolled back, so nothing of the run's own is on the attempt, and the run stops without committing or cleaning up.
    [Fact]
    public async Task A_reset_after_a_failed_checkpoint_write_and_before_its_recovery_is_never_overwritten()
    {
        await using var run = await ScriptedReviewRun.CreateAsync();
        run.OnPhase = async (phase, _, _, _) =>
        {
            if (phase == "verify")
            {
                await run.FailAttemptWritesAsync("verify-completed");
            }
            return null;
        };
        // The failure path logs the run's error before its retained save: the reset lands there, and the write failure
        // ends with it, so a save that skipped the claim check would now write over the reset.
        run.ExecuteWhenLogged(
            line =>
                line.Contains("run failed at", StringComparison.Ordinal)
                && line.Contains("transient attempt write failure", StringComparison.Ordinal),
            ScriptedReviewRun.EndAttemptWriteFailure + run.ResetSql
        );

        await FluentActions.Invoking(() => run.RunAsync()).Should().ThrowAsync<ReviewRunner.SlotClaimLostException>();

        var attempt = await run.ReadAsync(run.AttemptKey);
        attempt["stage"]!.GetValue<string>().Should().Be("failed-released", "the reset's record stands");
        attempt["cleanupState"]!.GetValue<string>().Should().Be("released");
        attempt["resetCount"]!.GetValue<int>().Should().Be(1);
        (await run.ReadAsync(run.OwnerKey))["state"]!.GetValue<string>().Should().Be("released");
        run.Phases.Should().NotContain("commit").And.NotContain("cleanup");
        (await run.Store.GetAsync<JsonObject>(run.RunKey)).Should().BeNull("nothing completed");
    }

    // The commit lands while a sweep's reset holds its fence on the claim: in its first save ("commit-save"), or after
    // it, when the completion write then fails ("failure-path"). The reset then fails and keeps its fence. The landed
    // result is kept, but not as released: released, the next sweep would pass the slot over as already released and
    // the slot would stay fenced for good. The next sweep resets and releases it.
    [Theory]
    [InlineData("commit-save")]
    [InlineData("failure-path")]
    public async Task A_commit_that_lands_under_a_reset_that_then_fails_is_kept_owned_for_the_next_sweep(string path)
    {
        await using var run = await ScriptedReviewRun.CreateAsync();
        var fence =
            $"UPDATE cache SET value = json_set(value, '$.{SlotRelease.ResetFence}', 'failed-reset') "
            + $"WHERE key = '{run.OwnerKey}';";
        if (path == "commit-save")
        {
            run.OnPhase = async (phase, _, _, _) =>
            {
                if (phase == "commit")
                {
                    await run.ExecuteAsync(fence);
                }
                return null;
            };
        }
        else
        {
            await run.ExecuteAsync(
                $"""
                CREATE TRIGGER fence_after_commit_save AFTER UPDATE ON cache
                WHEN NEW.key = '{run.AttemptKey}' AND json_extract(NEW.value, '$.stage') = 'commit-completed'
                BEGIN {fence} END;
                CREATE TRIGGER fail_completion BEFORE INSERT ON cache WHEN NEW.key = '{run.RunKey}'
                BEGIN SELECT RAISE(ABORT, 'completion write failed'); END;
                """
            );
        }

        var failure = await FluentActions.Invoking(() => run.RunAsync()).Should().ThrowAsync<Exception>();

        if (path == "commit-save")
        {
            failure.Which.Should().BeOfType<ReviewRunner.SlotClaimLostException>("cleanup found the fence");
        }
        else
        {
            failure
                .Which.Should()
                .BeOfType<SqliteException>()
                .Which.Message.Should()
                .Contain("completion write failed");
        }
        var kept = await run.ReadAsync(run.AttemptKey);
        kept["cleanupState"]!.GetValue<string>().Should().Be("owned", "the reset never released the slot");
        kept["artifactCommit"]!["commit"]!.GetValue<string>().Should().Be(new string('c', 40));
        run.Phases.Should().NotContain("cleanup");
        var owner = await run.ReadAsync(run.OwnerKey);
        owner[SlotRelease.ResetFence]!.GetValue<string>().Should().Be("failed-reset");
        // The failed reset put back a lapsed lease; the next sweep retries it.
        await run.ExecuteAsync(
            $"UPDATE cache SET value = json_set(value, '$.leaseExpiresAt', "
                + $"'{DateTimeOffset.UtcNow.AddHours(-1):O}') WHERE key = '{run.OwnerKey}';"
        );
        var handler = new FakeHttpMessageHandler()
            .OnJson(HttpMethod.Post, "/freeze", """{"frozen":true}""")
            .OnJson(HttpMethod.Get, "/run-state", """{"isInProgress":false,"currentRunId":null}""")
            .OnJson(HttpMethod.Get, "/subagents?recursive=true", """{"schemaVersion":1,"nodes":[]}""");
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var resets = 0;

        var results = await SlotReaper.SweepAsync(
            run.Connection,
            new LmStreamingS2SClient(http, null, null, null),
            async (phase, args, _, _) =>
            {
                await Task.Yield();
                if (phase != "cleanup")
                {
                    return [];
                }
                resets++;
                return new JsonObject
                {
                    ["claimToken"] = args["claimToken"]!.DeepClone(),
                    ["cleanBaselineVerified"] = true,
                    ["forced"] = new JsonArray(),
                };
            },
            "workspace",
            DateTimeOffset.UtcNow,
            undatedExpired: false,
            execute: true,
            CancellationToken.None,
            (_, _) => Task.CompletedTask,
            reads: new AbandonedChildProbe.ReadLog()
        );

        results.Should().ContainSingle(result => result.Status == "reclaimed");
        resets.Should().Be(1);
        var released = await run.ReadAsync(run.OwnerKey);
        released["state"]!.GetValue<string>().Should().Be("released");
        released.ContainsKey(SlotRelease.ResetFence).Should().BeFalse();
        var archived = await run.ReadAsync(run.AttemptKey);
        archived["cleanupState"]!.GetValue<string>().Should().Be("released");
        archived["artifactCommit"]!["commit"]!.GetValue<string>().Should().Be(new string('c', 40));
    }

    // The commit lands after a reset released the slot and another run claimed it under a new token: in the commit's
    // own save ("commit-save"), or after it, when the completion write then fails ("failure-path"). This run's result
    // is kept as released, with its archive, since its claim is gone; the new owner is not touched and this run cleans
    // nothing on a slot it no longer holds.
    [Theory]
    [InlineData("commit-save")]
    [InlineData("failure-path")]
    public async Task A_commit_that_lands_after_another_run_took_the_slot_is_kept_released_and_leaves_that_owner_alone(
        string path
    )
    {
        await using var run = await ScriptedReviewRun.CreateAsync();
        var replace =
            "UPDATE cache SET value = json_set(value, '$.claimToken', 'replacement-token', '$.state', 'owned') "
            + $"WHERE key = '{run.OwnerKey}';";
        if (path == "commit-save")
        {
            run.OnPhase = async (phase, _, _, _) =>
            {
                if (phase == "commit")
                {
                    await run.ExecuteAsync(replace);
                }
                return null;
            };
        }
        else
        {
            await run.ExecuteAsync(
                $"""
                CREATE TRIGGER replace_after_commit_save AFTER UPDATE ON cache
                WHEN NEW.key = '{run.AttemptKey}' AND json_extract(NEW.value, '$.stage') = 'commit-completed'
                BEGIN {replace} END;
                CREATE TRIGGER fail_completion BEFORE INSERT ON cache WHEN NEW.key = '{run.RunKey}'
                BEGIN SELECT RAISE(ABORT, 'completion write failed'); END;
                """
            );
        }

        if (path == "commit-save")
        {
            // The landed result is final: the run completes it, without cleaning a slot it no longer holds.
            (await run.RunAsync())
                .Should()
                .Be(0);
            (await run.ReadAsync(run.RunKey))["cleanupState"]!.GetValue<string>().Should().Be("released");
            // Nor does it release the thread's sandbox: after the reset, a newer attempt may be using that thread.
            run.Phases.Should().NotContain(phase => phase.StartsWith("sandbox-release", StringComparison.Ordinal));
        }
        else
        {
            var failure = await FluentActions.Invoking(() => run.RunAsync()).Should().ThrowAsync<SqliteException>();
            failure.Which.Message.Should().Contain("completion write failed");
        }
        var kept = await run.ReadAsync(run.AttemptKey);
        kept["stage"]!.GetValue<string>().Should().Be("completed-verified", "the commit landed");
        kept["cleanupState"]!.GetValue<string>().Should().Be("released", "this run's claim is gone");
        kept["artifactCommit"]!["commit"]!.GetValue<string>().Should().Be(new string('c', 40));
        var owner = await run.ReadAsync(run.OwnerKey);
        owner["claimToken"]!.GetValue<string>().Should().Be("replacement-token");
        owner["state"]!.GetValue<string>().Should().Be("owned", "the new owner keeps its slot");
        owner.ContainsKey(SlotRelease.ResetFence).Should().BeFalse();
        run.Phases.Should().NotContain("cleanup", "the slot belongs to another run");
    }

    // The commit lands under a reset's fence, so its save keeps the attempt owned (KeepLandedAsync), and the reset then
    // releases the slot before the run writes its completion. The completion must not put back the owned state the
    // reset replaced: the result is kept as released, the batch reads it as verified, and the run cleans nothing.
    [Fact]
    public async Task A_reset_that_releases_after_the_landed_save_is_not_reowned_by_the_completion()
    {
        await using var run = await ScriptedReviewRun.CreateAsync();
        run.OnPhase = async (phase, _, _, _) =>
        {
            if (phase == "commit")
            {
                await run.ExecuteAsync(
                    $"UPDATE cache SET value = json_set(value, '$.{SlotRelease.ResetFence}', 'reset') "
                        + $"WHERE key = '{run.OwnerKey}';"
                );
            }
            return null;
        };
        await run.ExecuteAsync(
            $"""
            CREATE TRIGGER release_after_landed_save AFTER UPDATE ON cache
            WHEN NEW.key = '{run.AttemptKey}' AND json_extract(NEW.value, '$.stage') = 'commit-completed'
                AND json_extract(NEW.value, '$.cleanupState') = 'owned'
            BEGIN
                UPDATE cache
                SET value = json_remove(json_set(value, '$.state', 'released'), '$.{SlotRelease.ResetFence}')
                WHERE key = '{run.OwnerKey}';
                UPDATE cache SET value = json_set(value, '$.cleanupState', 'released') WHERE key = '{run.AttemptKey}';
            END;
            """
        );

        (await run.RunAsync()).Should().Be(0, "the landed result is final");

        var kept = await run.ReadAsync(run.AttemptKey);
        kept["stage"]!.GetValue<string>().Should().Be("completed-verified");
        kept["cleanupState"]!.GetValue<string>().Should().Be("released", "the reset released the slot");
        kept["reviewedHeadSha"]!.GetValue<string>().Should().Be(new string('a', 40), "the completion's head is kept");
        kept["finishedAt"].Should().NotBeNull("the completion's time is kept");
        kept["artifactCommit"]!["commit"]!.GetValue<string>().Should().HaveLength(40, "the landed archive is kept");
        (await run.ReadAsync(run.RunKey))["cleanupState"]!.GetValue<string>().Should().Be("released");
        var entry = new JsonObject { ["head"] = new string('a', 40) };
        ReviewBatch.Verified(kept, entry).Should().BeTrue();
        ReviewBatch.Reusable(kept).Should().BeTrue("the batch reads it as verified, not as an owner to resume");
        (await run.ReadAsync(run.OwnerKey))["state"]!.GetValue<string>().Should().Be("released");
        run.Phases.Should().NotContain("cleanup", "the reset cleaned the slot");
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
                await run.FailAttemptWritesAsync("commit-completed");
            }
            return ScriptedReviewRun.Committed();
        };
        if (path == "write-failed")
        {
            // A transient failure: it ends once the failure path has logged it, before its retained save.
            run.ExecuteWhenLogged(
                line =>
                    line.Contains("run failed at", StringComparison.Ordinal)
                    && line.Contains("transient attempt write failure", StringComparison.Ordinal),
                ScriptedReviewRun.EndAttemptWriteFailure
            );
        }

        var failure = await FluentActions.Invoking(() => run.RunAsync()).Should().ThrowAsync<Exception>();

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
        // The lease as it stood when prepare refused: the saved claim records the failure without renewing it, since no
        // step runs on the claim any more.
        var lease = DateTimeOffset.UtcNow.AddMinutes(5);
        run.OnPhase = async (phase, _, _, _) =>
        {
            if (phase == "prepare")
            {
                await run.ExecuteAsync(
                    $"""
                    UPDATE cache SET value = json_set(value, '$.leaseExpiresAt', '{lease:O}') WHERE key = '{run.OwnerKey}';
                    CREATE TRIGGER fail_release BEFORE UPDATE ON cache
                    WHEN NEW.key = '{run.OwnerKey}' AND json_extract(NEW.value, '$.state') = 'released'
                    BEGIN SELECT RAISE(ABORT, 'release write failed'); END;
                    """
                );
                throw refused;
            }
            return null;
        };

        var failure = await FluentActions.Invoking(() => run.RunAsync()).Should().ThrowAsync<Exception>();

        failure.Which.Should().BeSameAs(refused, "prepare's own error, not the release's");
        var attempt = await run.ReadAsync(run.AttemptKey);
        attempt["cleanupError"]!.GetValue<string>().Should().Contain("release write failed");
        attempt["stage"]!.GetValue<string>().Should().Be("prepare-pending");
        var owner = await run.ReadAsync(run.OwnerKey);
        owner["state"]!.GetValue<string>().Should().Be("owned");
        owner["claimToken"]!.GetValue<string>().Should().Be(attempt["claimToken"]!.GetValue<string>());
        owner["leaseExpiresAt"]!.GetValue<DateTimeOffset>().Should().Be(lease, "the retained save did not renew it");
    }

    // Prepare refused the slot, and a sweep reset it before this run released its own claim: the release is refused and
    // the save that would record why finds the claim gone. The run stops on the lost claim, and its error still names
    // prepare's refusal, which nothing else recorded.
    [Fact]
    public async Task A_refused_prepare_whose_claim_was_reset_stops_with_the_refusal_in_its_error()
    {
        await using var run = await ScriptedReviewRun.CreateAsync();
        run.OnPhase = async (phase, _, _, _) =>
        {
            if (phase == "prepare")
            {
                await run.ExecuteAsync(run.ResetSql);
                throw new ReviewRunner.SlotNotIdleException("Native slot retains an owner.");
            }
            return null;
        };

        var lost = await FluentActions
            .Invoking(() => run.RunAsync())
            .Should()
            .ThrowAsync<ReviewRunner.SlotClaimLostException>();

        lost.Which.Message.Should()
            .Contain("refused prepare (Native slot retains an owner.)")
            .And.Contain("exact-token release refused");
        var attempt = await run.ReadAsync(run.AttemptKey);
        attempt["stage"]!.GetValue<string>().Should().Be("failed-released", "the reset's record stands");
        attempt["cleanupState"]!.GetValue<string>().Should().Be("released");
    }
}
