extern alias Reviewd;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using Microsoft.Data.Sqlite;
using Reviewd::Reviewd.Sample;

namespace CodeReviewDaemon.Sample.Tests.Scenarios;

/// <summary>These tests swap the process-wide Console writers, so they run on their own, not beside other tests.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ConsoleCapturingCollection
{
    public const string Name = "console-capturing";
}

/// <summary>
///     judge close through its CLI entry (<see cref="JudgeClose.RunAsync(CliOptions, CancellationToken, JudgeClose.Phase?)" />):
///     the gate on a temp database and store, the exit codes with scripted phases, and the store left untouched by a
///     real preview and execute.
/// </summary>
[Collection(ConsoleCapturingCollection.Name)]
public sealed class ReviewdJudgeCloseRunTests : IDisposable
{
    private const string Repo = "O365Exchange/Weve_DA/Nova";
    private const string Pr = "5776213";
    private const string ReviewDir = "PRs/nova-5776213";
    private readonly string _dir = Directory.CreateTempSubdirectory("rj-close-run-").FullName;

    private string Store => Path.Combine(_dir, "store");
    private string Db => Path.Combine(_dir, "reviews.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        RealGitFixture.ForceDelete(_dir);
    }

    [Fact]
    public async Task A_named_completed_final_judgment_passes_the_gate()
    {
        Save((4, Judged(Store, ReviewDir, 4)), (5, Judged(Store, ReviewDir, 5)));
        var phases = new Phases(Store);

        var (exit, _, _) = await Close(phases, "--judgment", "4");

        exit.Should().Be(0);
        phases.Prepared.Should().Equal(4);
    }

    [Theory]
    [InlineData("capped", "final", "judgment 4 is not a completed final judgment.")]
    [InlineData("completed", "draft", "judgment 4 is not a completed final judgment.")]
    [InlineData("judge-running", "", "judgment 4 is not a completed final judgment.")]
    public async Task A_judgment_that_is_not_completed_and_final_is_refused(string stage, string output, string reason)
    {
        var receipt = Judged(Store, ReviewDir, 4);
        receipt["stage"] = stage;
        receipt["outputStage"] = output;
        Save((4, receipt));
        var phases = new Phases(Store);

        var (exit, _, err) = await Close(phases, "--judgment", "4");

        exit.Should().Be(1);
        err.Should().Contain($"refused: {reason}");
        phases.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task By_default_a_judgment_that_still_runs_blocks_close()
    {
        var running = Judged(Store, ReviewDir, 5);
        running["stage"] = "judge-running";
        Save((4, Judged(Store, ReviewDir, 4)), (5, running));
        var phases = new Phases(Store);

        var (exit, _, err) = await Close(phases);

        exit.Should().Be(1);
        err.Should().Contain("refused: judgment 5 is still running; close after it ends or pass --judgment.");
        phases.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task A_judgment_json_that_differs_from_its_receipt_is_refused()
    {
        Save((4, Judged(Store, ReviewDir, 4)));
        File.AppendAllText(Path.Combine(Store, ReviewDir, "judgment-4", "judgment.json"), " ");
        var phases = new Phases(Store);

        var (exit, _, err) = await Close(phases, "--judgment", "4");

        exit.Should().Be(1);
        err.Should()
            .Contain($"refused: {ReviewDir}/judgment-4/judgment.json is missing or differs from the judge receipt.");
        phases.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task The_default_judgment_is_the_only_completed_final_one_Q_C1()
    {
        var capped = Judged(Store, ReviewDir, 6);
        capped["stage"] = "capped";
        Save((4, Judged(Store, ReviewDir, 4)), (6, capped));
        var phases = new Phases(Store);

        var (exit, _, _) = await Close(phases);

        exit.Should().Be(0);
        phases.Prepared.Should().Equal(4);
    }

    [Fact]
    public async Task With_two_completed_final_judgments_close_needs_judgment_and_lists_them_Q_C1()
    {
        // Seen in manual testing: the default picked test judgment 7, and one execute would have locked the PR
        // (no re-closing).
        Save((4, Judged(Store, ReviewDir, 4)), (5, Judged(Store, ReviewDir, 5)), (7, Judged(Store, ReviewDir, 7)));
        var phases = new Phases(Store);

        var (exit, _, err) = await Close(phases);

        exit.Should().Be(1);
        err.Should()
            .Contain("refused: judgments 4, 5, 7 are completed final judgments; pass --judgment <n> to pick one.");
        phases.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task A_packet_that_differs_from_the_one_the_judge_read_is_refused_D_C4()
    {
        Save((4, Judged(Store, ReviewDir, 4)));
        var packet = Path.Combine(Store, ReviewDir, "judgment-4", "inputs", "packet.json");
        var moved = JsonNode.Parse(File.ReadAllText(packet))!;
        moved["review"]!["rounds"]![0]!["tip"] = new string('e', 40);
        File.WriteAllText(packet, moved.ToJsonString());
        var phases = new Phases(Store);

        var (exit, _, err) = await Close(phases, "--judgment", "4");

        exit.Should().Be(1);
        err.Should()
            .Contain(
                $"refused: {ReviewDir}/judgment-4/inputs/packet.json is not the packet the judge read (judgment.json inputs.packetSha256)."
            );
        phases.Calls.Should().BeEmpty();
    }

    // The judge can write inputs/packet.json and judgment.json alike, so a matching pair proves nothing about
    // which PR the packet names: it must name this PR, the receipt's judgment dir and this PR's review branches.
    [Fact]
    public async Task A_packet_rewritten_to_another_PR_is_refused_before_anything_runs()
    {
        var other = new JsonArray(new JsonObject { ["branch"] = "review/nova-5790778", ["tip"] = new string('b', 40) });
        (Action<JsonObject> Forge, string Says)[] forgeries =
        [
            (
                packet => packet["identity"]!["reviewDirectory"] = "PRs/nova-5790778",
                $"the packet's review dir PRs/nova-5790778 does not hold judgment 4 ({ReviewDir}/judgment-4)."
            ),
            (packet => packet["identity"]!["prId"] = "5790778", "the packet is for PR 5790778, not 5776213."),
            (
                packet => packet["review"]!["rounds"] = other.DeepClone(),
                $"round branch review/nova-5790778 is not a review branch of {ReviewDir}."
            ),
        ];
        foreach (var (forge, says) in forgeries)
        {
            SqliteConnection.ClearAllPools();
            File.Delete(Db);
            Save((4, Judged(Store, ReviewDir, 4, forge: forge)));
            var phases = new Phases(Store);

            var (exit, _, err) = await Close(phases, "--judgment", "4");

            exit.Should().Be(1);
            err.Should().Contain($"refused: {says}");
            phases.Calls.Should().BeEmpty();
        }
    }

    // judge-inputs records the packet's sha256 in the receipt, which the judge cannot write.
    [Fact]
    public async Task A_packet_that_differs_from_the_one_judge_inputs_wrote_is_refused()
    {
        var written = Judged(Store, ReviewDir, 4);
        var packet = File.ReadAllBytes(Path.Combine(Store, ReviewDir, "judgment-4", "inputs", "packet.json"));
        written["inputs"] = new JsonObject { ["packetSha256"] = Convert.ToHexStringLower(SHA256.HashData(packet)) };
        var rewritten = Judged(Store, ReviewDir, 5, forge: packet => packet["cost"]!["totalUsd"] = 0.01m);
        rewritten["inputs"] = written["inputs"]!.DeepClone();
        Save((4, written), (5, rewritten));

        var (kept, _, _) = await Close(new Phases(Store), "--judgment", "4");
        var phases = new Phases(Store);
        var (exit, _, err) = await Close(phases, "--judgment", "5");

        kept.Should().Be(0);
        exit.Should().Be(1);
        err.Should()
            .Contain(
                $"refused: {ReviewDir}/judgment-5/inputs/packet.json is not the packet judge-inputs wrote (the receipt's inputs.packetSha256)."
            );
        phases.Calls.Should().BeEmpty();
    }

    [Theory]
    [InlineData(
        "already-closed",
        "planned",
        0,
        "already closed: main mmmmmmmmm has judgment 4's CLOSING.md and every round."
    )]
    [InlineData("refused", "planned", 1, "refused: the fake refused")]
    [InlineData("ready", "refused", 1, "refused: PRs/x matches a credential pattern; nothing was committed")]
    [InlineData("ready", "planned", 0, "\"status\":\"planned\"")]
    public async Task Exit_codes_through_RunAsync(string prepare, string commit, int expected, string says)
    {
        Save((4, Judged(Store, ReviewDir, 4)));
        var phases = new Phases(Store) { Prepare = prepare };
        phases.Commits.Enqueue(commit);

        var (exit, output, err) = await Close(phases, "--judgment", "4");

        exit.Should().Be(expected);
        (output + err).Should().Contain(says);
    }

    [Fact]
    public async Task A_main_that_keeps_moving_is_redone_three_times_then_exits_6_with_a_JSON_line()
    {
        Save((4, Judged(Store, ReviewDir, 4)));
        var phases = new Phases(Store);
        foreach (var _ in Enumerable.Range(0, 3))
            phases.Commits.Enqueue("main-moved");

        var (exit, output, err) = await Close(phases, "--judgment", "4");

        exit.Should().Be(6);
        phases.Prepared.Should().Equal(4, 4, 4);
        err.Should().Contain("main kept moving; nothing was pushed. Run close again.");
        var json = JsonNode.Parse(output.Split('\n', StringSplitOptions.RemoveEmptyEntries)[^1])!;
        json["status"]!.GetValue<string>().Should().Be("main-kept-moving");
        json["tries"]!.GetValue<int>().Should().Be(3);
    }

    [Fact]
    public async Task A_main_that_moved_once_is_fetched_again_and_closed_in_process()
    {
        Save((4, Judged(Store, ReviewDir, 4)));
        var phases = new Phases(Store);
        phases.Commits.Enqueue("main-moved");
        phases.Commits.Enqueue("closed");

        var (exit, output, err) = await Close(phases, "--judgment", "4", "--execute");

        exit.Should().Be(0);
        phases.Prepared.Should().Equal(4, 4);
        err.Should().Contain("main moved since the fetch (try 1 of 3); fetching the new main");
        output.Should().Contain("\"status\":\"closed\"");
    }

    [Fact]
    public async Task Close_prints_the_leftovers_it_swept_D_C6()
    {
        Save((4, Judged(Store, ReviewDir, 4)));
        var phases = new Phases(Store);
        phases.Removed.Add(
            new JsonObject
            {
                ["nonce"] = "0123456789abcdef0123456789abcdef",
                ["refs"] = 2,
                ["scratch"] = true,
                ["ageMinutes"] = 125,
            }
        );

        var (exit, _, err) = await Close(phases, "--judgment", "4");

        exit.Should().Be(0);
        phases.Calls[0].Should().Be("close-sweep");
        err.Should()
            .Contain("removed a leftover close (01234567…, 125 min old): 2 private refs and its scratch directory");
    }

    [Fact]
    public async Task A_missing_database_gets_a_plain_message()
    {
        var phases = new Phases(Store);

        var (exit, _, err) = await Close(phases);

        exit.Should().Be(1);
        err.Should().Contain($"there is no database at {Db}.").And.NotContain("SQLite Error");
    }

    [Fact]
    public async Task A_wrong_repo_says_no_judgment_exists_for_that_repo_and_PR()
    {
        Save((4, Judged(Store, ReviewDir, 4)));
        var phases = new Phases(Store);

        var (exit, _, err) = await Close(phases, "--judgment", "4", "--repo", "O365Exchange/Weve_DA/NovaClient");

        exit.Should().Be(1);
        err.Should().Contain("refused: no judgment exists for NovaClient !5776213 in this database.");
    }

    [Fact]
    public async Task The_first_line_uses_plain_plurals()
    {
        Save((4, Judged(Store, ReviewDir, 4)));

        var (_, output, _) = await Close(new Phases(Store), "--judgment", "4");

        output
            .ReplaceLineEndings("\n")
            .Split('\n')[0]
            .Should()
            .EndWith("0 knowledge, 1 improvement; 1 round of PRs/nova-5776213");
    }

    [Fact]
    public async Task A_failure_after_prepare_abandons_the_close_and_exits_1()
    {
        Save((4, Judged(Store, ReviewDir, 4)));
        var phases = new Phases(Store) { Prepare = "ready-without-overridden" };

        var (exit, _, err) = await Close(phases, "--judgment", "4");

        exit.Should().Be(1);
        err.Should().StartWith($"{Repo} !{Pr}: ");
        phases.Calls.Should().Equal("close-sweep", "close-prepare", "close-commit");
        phases.Abandoned.Should().Be(1, "the private refs and scratch of a failed close are removed");
    }

    // A Ctrl+C just as prepare's python printed "ready" makes the prepare call throw
    // (dotnet stops waiting), and the abandon did not cover that call: 2 private refs and 101 MB of scratch stayed
    // until a sweep an hour later.
    [Fact]
    public async Task A_cancel_as_prepare_finishes_still_abandons_that_close()
    {
        Save((4, Judged(Store, ReviewDir, 4)));
        using var cancel = new CancellationTokenSource();
        var phases = new Phases(Store) { Prepare = "ready-then-cancelled", Cancel = cancel };

        var close = () => Close(phases, cancel.Token, "--judgment", "4");

        (await close.Should().ThrowAsync<OperationCanceledException>())
            .Which.Message.Should()
            .Be("Cancelled before the commit; nothing was committed or pushed.");
        phases.Calls.Should().Equal("close-sweep", "close-prepare", "close-commit");
        phases.AbandonedNonces.Should().Equal(phases.PreparedNonces);
    }

    // A Ctrl+C after prepare answered and before the commit started was absorbed: close composed, committed and
    // pushed, then said the Ctrl+C "came during the commit". Nothing irreversible has begun there, so it cancels,
    // and the message Program prints says nothing was pushed (it used to say the push "may have landed").
    [Fact]
    public async Task A_cancel_before_the_commit_starts_commits_nothing()
    {
        Save((4, Judged(Store, ReviewDir, 4)));
        using var cancel = new CancellationTokenSource();
        var phases = new Phases(Store) { Prepare = "ready-then-ctrl-c", Cancel = cancel };

        var close = () => Close(phases, cancel.Token, "--judgment", "4", "--execute");

        (await close.Should().ThrowAsync<OperationCanceledException>("Program turns it into exit 130"))
            .Which.Message.Should()
            .Be("Cancelled before the commit; nothing was committed or pushed.");
        phases.Calls.Should().Equal("close-sweep", "close-prepare", "close-commit");
        phases.Abandoned.Should().Be(1, "the one close-commit call is the abandon; no commit ran");
        phases.AbandonedNonces.Should().Equal(phases.PreparedNonces);
    }

    // A SIGINT that reaches only dotnet during the commit said "Cancelled. If the
    // push had started it may have landed", and python then pushed. The commit is now waited for and its real
    // outcome reported.
    [Fact]
    public async Task A_cancel_during_the_commit_waits_for_it_and_reports_the_push()
    {
        Save((4, Judged(Store, ReviewDir, 4)));
        using var cancel = new CancellationTokenSource();
        var phases = new Phases(Store) { Cancel = cancel };
        phases.Commits.Enqueue("closed-despite-cancel");

        var (exit, output, err) = await Close(phases, cancel.Token, "--judgment", "4", "--execute");

        exit.Should().Be(0);
        output.Should().Contain("\"status\":\"closed\"");
        err.Should().NotContain("Cancelled");
        err.Should().Contain("Ctrl+C came during the commit; close waited for it to finish. What it did is below.");
        phases.AbandonedNonces.Should().Equal(phases.PreparedNonces);
    }

    // A Ctrl+C that also stops the commit's python (a terminal sends it to both) leaves the push
    // unknown, so close still ends cancelled, and only this cancel says "if the push had started it may have landed".
    [Fact]
    public async Task A_commit_the_cancel_also_stopped_still_ends_cancelled()
    {
        Save((4, Judged(Store, ReviewDir, 4)));
        using var cancel = new CancellationTokenSource();
        var phases = new Phases(Store) { Cancel = cancel };
        phases.Commits.Enqueue("interrupted");

        var close = () => Close(phases, cancel.Token, "--judgment", "4", "--execute");

        (await close.Should().ThrowAsync<OperationCanceledException>())
            .Which.Message.Should()
            .Be(
                "Cancelled. If the push had started it may have landed; run close again (it says already closed or "
                    + "finishes the close)."
            );
    }

    // A terminal Ctrl+C as the commit starts can kill its python before python's own cleanup runs (manual testing:
    // refs and scratch left in 3 of 6 runs). The abandon runs after every try, whatever the commit did; it removes
    // only that try's nonce.
    [Fact]
    public async Task A_commit_the_cancel_killed_still_abandons_that_close()
    {
        Save((4, Judged(Store, ReviewDir, 4)));
        using var cancel = new CancellationTokenSource();
        var phases = new Phases(Store) { Cancel = cancel };
        phases.Commits.Enqueue("interrupted");

        var close = () => Close(phases, cancel.Token, "--judgment", "4", "--execute");

        await close.Should().ThrowAsync<OperationCanceledException>();
        phases.Calls.Should().Equal("close-sweep", "close-prepare", "close-commit", "close-commit");
        phases.AbandonedNonces.Should().Equal(phases.PreparedNonces);
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Preview_and_execute_leave_the_store_untouched(bool execute)
    {
        Skip.If(await ReviewdSummaryReplyTests.FindPythonAsync() is not "python3", "no python3 on PATH");
        using var universe = ReviewdJudgeCloseTests.Universe.Create();
        var rounds = new JsonArray(
            new JsonObject { ["branch"] = "review/x", ["tip"] = universe.Round1 },
            new JsonObject { ["branch"] = "review/x-2e69ca4b", ["tip"] = universe.Round2 }
        );
        Save("Org/Proj/X", "1", (3, Judged(universe.Store, "PRs/x", 3, rounds, pr: "1")));
        universe.Rebaseline();
        string[] args =
        [
            "judge",
            "close",
            "--repo",
            "Org/Proj/X",
            "--pr",
            "1",
            "--db",
            Db,
            "--store",
            universe.Store,
            "--remote",
            universe.Origin,
            "--judgment",
            "3",
            .. execute ? new[] { "--execute" } : [],
        ];

        var (exit, output, err) = await Captured(() => JudgeClose.RunAsync(CliOptions.Parse(args), default, null));

        exit.Should().Be(0, err);
        output.Should().Contain(execute ? "\"status\":\"closed\"" : "\"status\":\"planned\"");
        universe.AssertClean(pushed: execute ? universe.Git(universe.Origin, "rev-parse", "main") : null);
        if (!execute)
            universe.Git(universe.Origin, "rev-parse", "main").Should().Be(universe.Main, "a preview pushes nothing");
        else
            universe
                .Git(universe.Origin, "rev-list", "--parents", "-n1", "main")
                .Split(' ')
                .Skip(1)
                .Should()
                .Equal(universe.Main, universe.Round1, universe.Round2);
    }

    // ---- fixture

    /// <summary>Writes judgment <paramref name="n" />'s files under <paramref name="store" /> and returns its receipt.</summary>
    internal static JsonObject Judged(
        string store,
        string reviewDir,
        int n,
        JsonArray? rounds = null,
        string pr = Pr,
        Action<JsonObject>? forge = null
    )
    {
        var dir = $"{reviewDir}/judgment-{n}";
        var packet = ReviewdJudgeCloseTests.Packet();
        packet["identity"]!["reviewDirectory"] = reviewDir;
        packet["identity"]!["prId"] = pr;
        if (rounds is not null)
            packet["review"]!["rounds"] = rounds;
        forge?.Invoke(packet);
        var packetText = packet.ToJsonString();
        var judgment = new JsonObject
        {
            ["stage"] = "final",
            ["verdict"] = "v",
            ["inputs"] = new JsonObject { ["packetSha256"] = Sha(packetText) },
            ["knowledge"] = new JsonArray(),
            ["improvements"] = new JsonArray(
                new JsonObject
                {
                    ["key"] = $"process-repo/x/{n}",
                    ["category"] = "process-repo",
                    ["change"] = "Do the check.",
                    ["evidence"] = new JsonArray("thread 1/2"),
                    ["writePolicy"] = "count-only",
                }
            ),
        };
        var files = new Dictionary<string, string>
        {
            ["inputs/packet.json"] = packetText,
            ["judgment.json"] = judgment.ToJsonString(),
            ["judgment.md"] = "# Judgment\n",
        };
        foreach (var (path, text) in files)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(store, dir, path))!);
            File.WriteAllText(Path.Combine(store, dir, path), text);
        }
        var receipt = ReviewdJudgeCloseTests.Run(1.5m);
        receipt["judgmentDir"] = dir;
        receipt["outputs"] = new JsonArray(
            new JsonObject { ["path"] = "judgment.json", ["sha256"] = Sha(files["judgment.json"]) },
            new JsonObject { ["path"] = "judgment.md", ["sha256"] = Sha(files["judgment.md"]) }
        );
        return receipt;
    }

    private static string Sha(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private void Save(params (int N, JsonObject Receipt)[] receipts) => Save(Repo, Pr, receipts);

    private void Save(string repo, string pr, params (int N, JsonObject Receipt)[] receipts)
    {
        using var connection = new SqliteConnection($"Data Source={Db};Pooling=False");
        connection.Open();
        using var create = connection.CreateCommand();
        create.CommandText = "CREATE TABLE IF NOT EXISTS cache (key TEXT PRIMARY KEY, value TEXT NOT NULL)";
        create.ExecuteNonQuery();
        foreach (var (n, receipt) in receipts)
        {
            using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO cache (key, value) VALUES (@key, @value)";
            insert.Parameters.AddWithValue(
                "@key",
                $"reviewd/judge-run/{PrCommands.ParseRepo(repo).NormalizedKey}/{pr}/{n}"
            );
            insert.Parameters.AddWithValue("@value", receipt.ToJsonString());
            insert.ExecuteNonQuery();
        }
    }

    private Task<(int Exit, string Out, string Err)> Close(Phases phases, params string[] extra) =>
        Close(phases, default, extra);

    private Task<(int Exit, string Out, string Err)> Close(Phases phases, CancellationToken ct, params string[] extra)
    {
        var repo = extra.Contains("--repo") ? [] : new[] { "--repo", Repo };
        var options = CliOptions.Parse(["judge", "close", .. repo, "--pr", Pr, "--db", Db, "--store", Store, .. extra]);
        return Captured(() => JudgeClose.RunAsync(options, ct, phases.RunAsync));
    }

    private static async Task<(int Exit, string Out, string Err)> Captured(Func<Task<int>> run)
    {
        var (stdout, stderr) = (Console.Out, Console.Error);
        using var output = new StringWriter();
        using var error = new StringWriter();
        Console.SetOut(output);
        Console.SetError(error);
        try
        {
            var exit = await run();
            return (exit, output.ToString(), error.ToString());
        }
        finally
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
        }
    }

    /// <summary>Scripted close phases: prepare answers <see cref="Prepare" />, each commit the next of <see cref="Commits" />.</summary>
    private sealed class Phases(string store)
    {
        public List<string> Calls { get; } = [];
        public List<int> Prepared { get; } = [];
        public string Prepare { get; init; } = "ready";
        public Queue<string> Commits { get; } = new();
        public JsonArray Removed { get; } = [];
        public int Abandoned { get; private set; }
        public List<string> PreparedNonces { get; } = [];
        public List<string> AbandonedNonces { get; } = [];

        /// <summary>The user's Ctrl+C: "ready-then-cancelled" and the cancel commits fire it.</summary>
        public CancellationTokenSource? Cancel { get; init; }

        public Task<JsonObject> RunAsync(string name, JsonObject args, CancellationToken ct)
        {
            Calls.Add(name);
            var main = new string('m', 40);
            return Task.FromResult(
                name switch
                {
                    "close-sweep" => new JsonObject { ["status"] = "swept", ["removed"] = Removed.DeepClone() },
                    "close-prepare" => PrepareFor(args, main, ct),
                    "close-commit" when args["abandon"] is not null => Abandon(args),
                    "close-commit" => (Commits.Count > 0 ? Commits.Dequeue() : "planned") switch
                    {
                        // Like the local python runner: the cancel stops dotnet's wait on the step (when the step
                        // was given the cancelled token); python itself goes on and pushes.
                        "closed-despite-cancel" => CancelThen(
                            ct,
                            () =>
                                new JsonObject
                                {
                                    ["status"] = "closed",
                                    ["commit"] = new string('c', 40),
                                    ["parents"] = new JsonArray(main),
                                    ["files"] = new JsonArray(),
                                }
                        ),
                        // A terminal Ctrl+C reaches python too: the step exits 130.
                        "interrupted" => CancelThen(
                            ct,
                            () =>
                                throw new InvalidOperationException(
                                    "close step close-commit failed (exit 130): KeyboardInterrupt"
                                )
                        ),
                        "main-moved" => new JsonObject
                        {
                            ["status"] = "main-moved",
                            ["detail"] = "! [rejected] main (fetch first)",
                        },
                        "refused" => new JsonObject
                        {
                            ["status"] = "refused",
                            ["reason"] = "PRs/x matches a credential pattern; nothing was committed",
                        },
                        var status => new JsonObject
                        {
                            ["status"] = status,
                            ["commit"] = new string('c', 40),
                            ["parents"] = new JsonArray(main),
                            ["files"] = new JsonArray(),
                        },
                    },
                    _ => throw new InvalidOperationException($"unexpected phase {name}"),
                }
            );
        }

        private JsonObject Abandon(JsonObject args)
        {
            Abandoned++;
            AbandonedNonces.Add(args["nonce"]!.GetValue<string>());
            return new JsonObject { ["status"] = "abandoned" };
        }

        private JsonObject CancelThen(CancellationToken ct, Func<JsonObject> step)
        {
            Cancel!.Cancel();
            ct.ThrowIfCancellationRequested();
            return step();
        }

        private JsonObject PrepareFor(JsonObject args, string main, CancellationToken ct)
        {
            Prepared.Add(args["judgment"]!.GetValue<int>());
            PreparedNonces.Add(args["nonce"]!.GetValue<string>());
            if (!Prepare.StartsWith("ready", StringComparison.Ordinal))
                return Prepare == "refused"
                    ? new JsonObject { ["status"] = "refused", ["reason"] = "the fake refused" }
                    : new JsonObject { ["status"] = Prepare, ["main"] = main };
            var scratch = $".scratch/reviewd-close/{args["nonce"]}";
            Directory.CreateDirectory(Path.Combine(store, scratch, "tree"));
            var ready = new JsonObject
            {
                ["status"] = "ready",
                ["main"] = main,
                ["scratch"] = scratch,
                ["overridden"] = new JsonArray(),
            };
            // A malformed answer makes close fail after prepare, before it commits.
            if (Prepare == "ready-without-overridden")
                ready.Remove("overridden");
            // Python printed "ready" and made its refs and scratch, then the cancel stopped dotnet's wait on it.
            if (Prepare == "ready-then-cancelled")
            {
                Cancel!.Cancel();
                ct.ThrowIfCancellationRequested();
            }
            // The Ctrl+C lands just after dotnet read "ready".
            if (Prepare == "ready-then-ctrl-c")
                Cancel!.Cancel();
            return ready;
        }
    }
}
