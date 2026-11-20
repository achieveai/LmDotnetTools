extern alias Reviewd;

using System.Text.Json.Nodes;
using AchieveAi.LmDotnetTools.Sandbox;
using CodeReviewDaemon.Sample.Agents;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using Microsoft.Data.Sqlite;
using Reviewd::Reviewd.Sample;

namespace CodeReviewDaemon.Sample.Tests.Scenarios;

/// <summary>
///     What the runner, the slot commands and the batch sweep tell an operator, read from the console they write to.
/// </summary>
[Collection(ConsoleCapturingCollection.Name)]
public sealed class ReviewdOperatorOutputTests : IDisposable
{
    private const string Nova = "O365Exchange/Weve_DA/Nova";
    private readonly TextWriter _stdout = Console.Out;
    private readonly TextWriter _stderr = Console.Error;
    private readonly StringWriter _out = new();
    private readonly StringWriter _err = new();

    public ReviewdOperatorOutputTests()
    {
        Console.SetOut(TextWriter.Synchronized(_out));
        Console.SetError(TextWriter.Synchronized(_err));
    }

    public void Dispose()
    {
        Console.SetOut(_stdout);
        Console.SetError(_stderr);
        _out.Dispose();
        _err.Dispose();
    }

    // The release of a refused prepare's claim fails, and so does saving the attempt that records why. The release
    // error is logged before that save, with prepare's own refusal, so neither is lost with it.
    [Fact]
    public async Task A_release_error_is_logged_before_the_save_that_records_it()
    {
        await using var run = await ScriptedReviewRun.CreateAsync();
        run.OnPhase = async (phase, _, _, _) =>
        {
            if (phase == "prepare")
            {
                await run.ExecuteAsync(
                    $"""
                    CREATE TRIGGER fail_attempt_insert BEFORE INSERT ON cache WHEN NEW.key = '{run.AttemptKey}'
                    BEGIN SELECT RAISE(ABORT, 'attempt write failed'); END;
                    CREATE TRIGGER fail_attempt_update BEFORE UPDATE ON cache WHEN NEW.key = '{run.AttemptKey}'
                    BEGIN SELECT RAISE(ABORT, 'attempt write failed'); END;
                    """
                );
                throw new ReviewRunner.SlotNotIdleException("Native slot retains an owner.");
            }
            return null;
        };

        await FluentActions.Invoking(() => run.RunAsync()).Should().ThrowAsync<SqliteException>();

        _err.ToString()
            .Should()
            .Contain("Claim release held its lane after prepare refused (Native slot retains an owner.): ")
            .And.Contain("attempt write failed");
    }

    // The commit landed, then the completion write fails, and so does the failure path's retained save of the landed
    // result: "released" when a reset had already released the claim (the receipt is saved as it is), "fenced" when a
    // reset still holds its fence (the landed result is kept owned). The run's own error is logged before that save, so
    // the save's failure, which ends the run, does not hide it.
    [Theory]
    [InlineData("released")]
    [InlineData("fenced")]
    public async Task A_run_error_is_logged_before_the_retained_save_that_could_hide_it(string claim)
    {
        await using var run = await ScriptedReviewRun.CreateAsync();
        await run.ExecuteAsync(
            $"""
            CREATE TRIGGER fail_completion BEFORE INSERT ON cache WHEN NEW.key = '{run.RunKey}'
            BEGIN SELECT RAISE(ABORT, 'completion write failed'); END;
            CREATE TRIGGER fail_retained_insert BEFORE INSERT ON cache
            WHEN NEW.key = '{run.AttemptKey}' AND json_extract(NEW.value, '$.stage') = 'completed-verified'
            BEGIN SELECT RAISE(ABORT, 'retained save failed'); END;
            CREATE TRIGGER fail_retained_update BEFORE UPDATE ON cache
            WHEN NEW.key = '{run.AttemptKey}' AND json_extract(NEW.value, '$.stage') = 'completed-verified'
            BEGIN SELECT RAISE(ABORT, 'retained save failed'); END;
            """
        );
        run.OnPhase = async (phase, args, _, token) =>
        {
            if (phase != "commit")
            {
                return null;
            }
            if (claim == "released")
            {
                await run.ResetAsync(args, token);
            }
            else
            {
                await run.ExecuteAsync(
                    $"UPDATE cache SET value = json_set(value, '$.{SlotRelease.ResetFence}', 'reset') "
                        + $"WHERE key = '{run.OwnerKey}';"
                );
            }
            return null;
        };

        var failure = await FluentActions.Invoking(() => run.RunAsync()).Should().ThrowAsync<SqliteException>();

        failure.Which.Message.Should().Contain("retained save failed");
        _err.ToString().Should().Contain("run failed at completed-verified: ").And.Contain("completion write failed");
        var attempt = await run.ReadAsync(run.AttemptKey);
        attempt["stage"]!.GetValue<string>().Should().Be("commit-completed", "only the commit's own save landed");
        attempt["cleanupState"]!.GetValue<string>().Should().Be(claim == "released" ? "released" : "owned");
    }

    // The commit's response is lost and the save of its replay id then fails (here, canceled). The run still ends with
    // the commit's own error, which the batch classifies, the failed save is logged beside it, and the failure path
    // saves the replay id. A lost claim still stops the run (A_commit_error_is_logged_even_when_the_lost_claim_stops_its_save).
    [Fact]
    public async Task A_commit_error_stays_the_outcome_when_saving_its_replay_id_fails()
    {
        await using var run = await ScriptedReviewRun.CreateAsync();
        string? commitOperation = null;
        run.OnPhase = async (phase, _, operation, _) =>
        {
            if (phase != "commit")
            {
                return null;
            }
            commitOperation = operation;
            await run.Cancellation.CancelAsync();
            throw new SandboxException(SandboxErrorKind.TransportTimeout, "commit transport timeout (00:00:30).");
        };

        var failure = await FluentActions.Invoking(() => run.RunAsync()).Should().ThrowAsync<SandboxException>();

        failure.Which.Message.Should().Contain("commit transport timeout");
        _err.ToString()
            .Should()
            .Contain($"commit, operation {commitOperation}, failed: commit transport timeout")
            .And.Contain($"commit, operation {commitOperation}, replay state not saved: ");
        var attempt = await run.ReadAsync(run.AttemptKey);
        attempt["stage"]!.GetValue<string>().Should().Be("verify-pending");
        attempt[ReviewRunner.CommitReplay]!.GetValue<string>().Should().Be(commitOperation);
        (await run.ReadAsync(run.OwnerKey))["state"]!.GetValue<string>().Should().Be("owned");
    }

    // reap-slots prints how many slots it held beside the results, and the count agrees with them: one verified review
    // without an archive is held, untouched, and one never verified is reclaimed.
    [Fact]
    public async Task Reap_slots_prints_a_held_count_that_agrees_with_its_results()
    {
        var database = Path.Combine(Path.GetTempPath(), $"reviewd-reap-held-{Guid.NewGuid():N}.db");
        try
        {
            using (var connection = PrThreadBindings.Connection(database))
            {
                await connection.OpenAsync();
                await SeedAsync(connection, 5, "100", "commit-pending");
                await SeedAsync(connection, 6, "101", "verify-pending");
            }
            var handler = new FakeHttpMessageHandler()
                .OnJson(HttpMethod.Post, "/freeze", """{"frozen":true}""")
                .OnJson(HttpMethod.Get, "/run-state", """{"isInProgress":false,"currentRunId":null}""")
                .OnJson(HttpMethod.Get, "/subagents?recursive=true", """{"schemaVersion":1,"nodes":[]}""");
            using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
            var resets = new List<string>();

            var exit = await SlotReaper.RunAsync(
                CliOptions.Parse([
                    "review",
                    "reap-slots",
                    "--db",
                    database,
                    "--workspace",
                    "workspace",
                    "--session",
                    "session",
                    "--execute",
                ]),
                new LmStreamingS2SClient(http, null, null, null),
                Reset(resets),
                CancellationToken.None
            );

            exit.Should().Be(0);
            var printed = JsonNode.Parse(LastLine(_out))!;
            printed["held"]!.GetValue<int>().Should().Be(1);
            printed["reclaimed"]!.GetValue<int>().Should().Be(1);
            var statuses = printed["results"]!.AsArray().Select(r => r!["Status"]!.GetValue<string>()).ToList();
            statuses.Count(s => s == "held").Should().Be(printed["held"]!.GetValue<int>());
            printed["results"]!.AsArray().Single(r => r!["Status"]!.GetValue<string>() == "held")!["Pr"]!
                .GetValue<string>()
                .Should()
                .Be("100");
            resets.Should().Equal("101");
        }
        finally
        {
            ReviewdSqlitePools.Clear(database);
            File.Delete(database);
        }
    }

    // The real reclaim-slot command on a held verified review: without --discard-verified it prints held and touches
    // nothing; with it but without --execute it previews would-reclaim and still resets nothing; with both it resets
    // the slot once, releases the owner and keeps the verified stage.
    [Fact]
    public async Task Reclaim_slot_discards_a_held_verified_review_only_when_told_to_and_previews_first()
    {
        var database = Path.Combine(Path.GetTempPath(), $"reviewd-reclaim-command-{Guid.NewGuid():N}.db");
        var repo = PrCommands.ParseRepo(Nova);
        var attemptKey = $"reviewd/review-attempt/{repo.NormalizedKey}/100";
        try
        {
            string ownerKey;
            using (var connection = PrThreadBindings.Connection(database))
            {
                await connection.OpenAsync();
                ownerKey = await SeedAsync(connection, 0, "100", "verify-pending", verifiedPublication: true);
            }
            var before = await RowsAsync(database, ownerKey, attemptKey);
            var handler = new FakeHttpMessageHandler()
                .OnJson(HttpMethod.Post, "/freeze", """{"frozen":true}""")
                .OnJson(HttpMethod.Get, "/run-state", """{"isInProgress":false,"currentRunId":null}""")
                .OnJson(HttpMethod.Get, "/subagents?recursive=true", """{"schemaVersion":1,"nodes":[]}""");
            using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
            var resets = new List<string>();
            async Task<JsonNode> ReclaimAsync(params string[] flags)
            {
                _out.GetStringBuilder().Clear();
                var exit = await ReclaimSlotCommand.RunAsync(
                    CliOptions.Parse([
                        "review",
                        "reclaim-slot",
                        "--repo",
                        Nova,
                        "--pr",
                        "100",
                        "--slot",
                        "0",
                        "--db",
                        database,
                        "--workspace",
                        "workspace",
                        "--session",
                        "session",
                        .. flags,
                    ]),
                    () => new LmStreamingS2SClient(http, null, null, null),
                    () => Reset(resets),
                    CancellationToken.None
                );
                exit.Should().Be(0);
                return JsonNode.Parse(LastLine(_out))!;
            }

            (await ReclaimAsync("--execute"))["Status"]!.GetValue<string>().Should().Be("held");
            (await RowsAsync(database, ownerKey, attemptKey)).Should().Equal(before);
            (await ReclaimAsync("--discard-verified"))["Status"]!.GetValue<string>().Should().Be("would-reclaim");
            resets.Should().BeEmpty("a preview never resets");
            (await RowsAsync(database, ownerKey, attemptKey)).Should().Equal(before);
            (await ReclaimAsync("--discard-verified", "--execute"))["Status"]!
                .GetValue<string>()
                .Should()
                .Be("reclaimed");

            resets.Should().Equal("100");
            var rows = await RowsAsync(database, ownerKey, attemptKey);
            JsonNode.Parse(rows[0])!["state"]!.GetValue<string>().Should().Be("released");
            var attempt = JsonNode.Parse(rows[1])!;
            attempt["stage"]!.GetValue<string>().Should().Be("verify-pending", "the verified stage is kept");
            attempt["verifiedPublication"].Should().NotBeNull();
            attempt["cleanupState"]!.GetValue<string>().Should().Be("released");
        }
        finally
        {
            ReviewdSqlitePools.Clear(database);
            File.Delete(database);
        }
    }

    // The batch says every held slot on every sweep, each with its PR, slot and the reason it is held.
    [Fact]
    public async Task The_batch_sweep_logs_each_held_slot_with_its_reason()
    {
        var database = Path.Combine(Path.GetTempPath(), $"reviewd-batch-held-{Guid.NewGuid():N}.db");
        try
        {
            using var connection = PrThreadBindings.Connection(database);
            await connection.OpenAsync();
            await SeedAsync(connection, 5, "100", "commit-pending");
            await SeedAsync(connection, 6, "101", "verify-completed");
            // A host that answers nothing: a held slot is decided before any call.
            using var http = new HttpClient(new FakeHttpMessageHandler())
            {
                BaseAddress = new Uri("http://localhost/"),
            };
            var resets = new List<string>();
            var results = await SlotReaper.SweepAsync(
                connection,
                new LmStreamingS2SClient(http, null, null, null),
                Reset(resets),
                "workspace",
                DateTimeOffset.UtcNow,
                undatedExpired: false,
                execute: true,
                CancellationToken.None,
                (_, _) => Task.CompletedTask,
                new AbandonedChildProbe.ReadLog()
            );

            ReviewBatch.LogSweep(results);

            var held = _err.ToString()
                .Split('\n')
                .Where(line => line.StartsWith("Reaper holding lapsed-lease slot ", StringComparison.Ordinal))
                .ToList();
            var name = PrCommands.ParseRepo(Nova).DisplayName;
            held.Should().HaveCount(2);
            held.Should()
                .ContainSingle(line =>
                    line.Contains($"{name}#100/slot5: verified review at commit-pending has no archive commit")
                );
            held.Should()
                .ContainSingle(line =>
                    line.Contains($"{name}#101/slot6: verified review at verify-completed has no archive commit")
                );
            resets.Should().BeEmpty();
        }
        finally
        {
            ReviewdSqlitePools.Clear(database);
            File.Delete(database);
        }
    }

    // A lapsed claim on slot for pr, whose attempt is at stage.
    private static async Task<string> SeedAsync(
        SqliteConnection connection,
        int slot,
        string pr,
        string stage,
        bool verifiedPublication = false
    )
    {
        var (ownerKey, store) = await SlotReaperTests.SeedLapsedClaimAsync(connection, slot, pr: pr);
        var attemptKey = $"reviewd/review-attempt/{PrCommands.ParseRepo(Nova).NormalizedKey}/{pr}";
        var attempt = (await store.GetAsync<JsonObject>(attemptKey))!;
        attempt["stage"] = stage;
        attempt["reviewInputId"] = "input-1";
        attempt["confirmationInputId"] = "input-2";
        if (verifiedPublication)
        {
            attempt["verifiedPublication"] = new JsonObject { ["comments"] = 3 };
        }
        await store.SetAsync(attemptKey, attempt, CancellationToken.None);
        return ownerKey;
    }

    // A reset that succeeds and records the PR it reset.
    private static SlotReaper.SandboxExecutor Reset(List<string> resets) =>
        (phase, args, _, _) =>
        {
            if (phase is "step-activity" or "step-stop")
            {
                return Task.FromResult(new JsonObject());
            }
            resets.Add(args["pr"]!.GetValue<string>());
            return Task.FromResult(
                new JsonObject
                {
                    ["claimToken"] = args["claimToken"]!.DeepClone(),
                    ["cleanBaselineVerified"] = true,
                    ["forced"] = new JsonArray(),
                }
            );
        };

    private static async Task<string[]> RowsAsync(string database, params string[] keys)
    {
        using var connection = PrThreadBindings.Connection(database);
        await connection.OpenAsync();
        var rows = new string[keys.Length];
        for (var i = 0; i < keys.Length; i++)
        {
            using var read = connection.CreateCommand();
            read.CommandText = "SELECT value FROM cache WHERE key=@key";
            read.Parameters.AddWithValue("@key", keys[i]);
            rows[i] = (string)(await read.ExecuteScalarAsync())!;
        }
        return rows;
    }

    private static string LastLine(StringWriter writer) =>
        writer.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Last().Trim();
}
