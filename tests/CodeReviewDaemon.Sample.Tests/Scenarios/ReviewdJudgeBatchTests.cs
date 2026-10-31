extern alias Reviewd;

using System.Net;
using System.Text.Json.Nodes;
using CodeReviewDaemon.Sample.Agents;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Reviewd::Reviewd.Sample;

namespace CodeReviewDaemon.Sample.Tests.Scenarios;

/// <summary>
///     <c>judge batch</c> selection over real temp SQLite stores and a scripted host: the scan reads them read-only
///     and the plan decides what may run. One test runs the CLI entry and reads its output, so the class runs apart.
/// </summary>
[Collection(ConsoleCapturingCollection.Name)]
public sealed class ReviewdJudgeBatchTests : IDisposable
{
    private const string Repo = "O365Exchange/Weve_DA/Nova";
    private const string Workspace = "ws-1";
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    private static readonly JudgeBatch.Settings Settings = new(5m, 30m, 4m, 7, "prompt-sha");
    private static readonly string RepoKey = PrCommands.ParseRepo(Repo).NormalizedKey;
    private readonly string _dir = Directory.CreateTempSubdirectory("judge-batch-").FullName;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public async Task An_unreadable_target_blocks_new_starts_everywhere_D_MT_5()
    {
        // alpha has a judge in flight on the host, but one corrupt row makes the whole store unreadable.
        var alpha = Db(
            "alpha",
            Judgment("7001", 1, Receipt("judge-running", thread: "thread-alpha")),
            ("reviewd/judge-run/" + RepoKey + "/7002/1", "{corrupt")
        );
        var beta = Db("beta", Review("8001"), Snapshot(("8001", "Merged", Now.AddDays(-1))));
        var host = new FakeHttpMessageHandler().OnJson(HttpMethod.Get, "thread-alpha/run-state", Active);

        var (scan, plan) = await PlanAsync(host, [], Target("alpha", alpha), Target("beta", beta));

        var damaged = new JudgeBatch.Note(
            "install alpha",
            "unusable because a judge record in its database is damaged"
        );
        scan.NeedsYou.Should().Contain(damaged);
        var start = plan.Single(entry => entry.Pick.Pr.Label == "Nova !8001");
        start.Pick.Kind.Should().Be(JudgeBatch.Kind.Start);
        start.Wait.Should().Be("waits: install alpha unusable");

        // The JSON line names each unusable install with a plain cause (the tick alerts from it); the
        // markdown says the same, and the parser's text stays in stderr.
        var summary = JudgeBatch.Summary(scan, plan, [], [], Settings, execute: false, "ok", 0, Now);
        JsonNode.Parse(summary.TrimEnd().Split('\n')[^1])!["unusable"]!
            .ToJsonString()
            .Should()
            .Be("""[{"name":"alpha","cause":"a judge record in its database is damaged"}]""");
        summary.Should().Contain($"- {damaged}").And.NotContain("reviewd/judge-run/");

        // A targets line the batch cannot use (here the tick's "name<TAB>reason" form) blocks starts the same way.
        var (_, planB) = await PlanAsync(
            host,
            [("viva-pulse", "its env files differ from Nova's")],
            Target("beta", beta)
        );
        planB.Single().Wait.Should().Be("waits: install viva-pulse unusable");
        JudgeBatch
            .ParseTargets(["viva-pulse\tits env files differ from Nova's", "x\ty\tz"])
            .Broken.Should()
            .Equal(
                ("viva-pulse", "its env files differ from Nova's"),
                ("on targets line 2", "expected name<TAB>db<TAB>session<TAB>workspace")
            );
    }

    [Theory]
    [InlineData("running", true)]
    [InlineData("completed", false)]
    public async Task A_capped_judge_whose_sub_agents_still_run_is_in_flight_R_B9(string child, bool blocks)
    {
        var capped = Receipt("capped", thread: "thread-capped");
        capped["frozenAt"] = Now.AddHours(-1);
        capped["descendantsSettled"] = false;
        capped["ado"] = new JsonObject { ["compare"] = "unchanged" };
        var alpha = Db(
            "alpha",
            Review("7001"),
            Review("7002"),
            Snapshot(("7001", "Merged", Now.AddDays(-2)), ("7002", "Merged", Now.AddDays(-1))),
            Judgment("7001", 1, capped)
        );
        var host = new FakeHttpMessageHandler()
            .OnJson(HttpMethod.Get, "thread-capped/run-state", Active)
            .OnJson(HttpMethod.Get, "thread-capped/messages", "[]")
            .OnJson(HttpMethod.Get, "thread-capped/subagents?recursive=true", Tree(child))
            .OnJson(HttpMethod.Get, "thread-capped/usage", """{"preferredCostMicros":5200000,"completeness":0}""");

        var (scan, plan) = await PlanAsync(host, [], Target("alpha", alpha));

        plan.Single(entry => entry.Pick.Kind == JudgeBatch.Kind.PostCheck).Wait.Should().BeNull();
        var start = plan.Single(entry => entry.Pick.Kind == JudgeBatch.Kind.Start);
        if (blocks)
        {
            scan.InFlight.Should().Equal("Nova !7001 (capped judge still settling)");
            start.Wait.Should().StartWith("waits: judge in flight on Nova !7001");
        }
        else
        {
            scan.InFlight.Should().BeEmpty();
            start.Wait.Should().BeNull();
        }
    }

    [Fact]
    public async Task A_live_judge_under_another_prompt_is_only_running_now_W_1()
    {
        // A by-hand run: judge-running under an older prompt, live on the host.
        var byHand = Receipt("judge-running", thread: "thread-old");
        byHand["identity"]!["promptTemplateSha256"] = "old-prompt";
        var alpha = Db(
            "alpha",
            Review("7001"),
            Snapshot(("7001", "Merged", Now.AddDays(-1))),
            Judgment("7001", 2, byHand)
        );
        const string Usage = """{"preferredCostMicros":1000000,"completeness":0}""";
        var live = new FakeHttpMessageHandler()
            .OnJson(HttpMethod.Get, "thread-old/run-state", Active)
            .OnJson(HttpMethod.Get, "thread-old/usage", Usage);

        var (scan, plan) = await PlanAsync(live, [], Target("alpha", alpha));

        scan.InFlight.Should().Equal("Nova !7001");
        scan.NeedsYou.Select(note => note.ToString()).Should().BeEmpty("a live judge shows only under Running now");
        plan.Should().BeEmpty("the batch never resumes a run under another prompt");
        JudgeBatch
            .Summary(scan, plan, [], [], Settings, execute: false, "ok", 0, Now)
            .Should()
            .Contain("Running now: Nova !7001.")
            .And.NotContain("resume it by hand");

        // Once the host says it is idle, the stopped run needs a person.
        var idle = new FakeHttpMessageHandler()
            .OnJson(HttpMethod.Get, "thread-old/run-state", Idle)
            .OnJson(HttpMethod.Get, "thread-old/usage", Usage);
        var (after, _) = await PlanAsync(idle, [], Target("alpha", alpha));
        after
            .NeedsYou.Should()
            .ContainSingle()
            .Which.What.Should()
            .EndWith("resume it by hand with the original prompt");
    }

    [Fact]
    public async Task An_unknown_cost_counts_its_cap_in_the_day_cap_F2()
    {
        // Unpriced (the runner freezes at once), live read failing, a complete reading, unmeasured with no host
        // usage (404), and measured with no dollars: $5 + $5 + $2 + $5 + $5.
        var unpriced = Receipt("capped", thread: "thread-unpriced", usd: null, finishedAt: Now.AddHours(-1));
        var unreadable = Receipt("completed", thread: "thread-down", usd: 1m, finishedAt: Now.AddHours(-1));
        unreadable["judgeUsage"]!["completeness"] = 0;
        var complete = Receipt("completed", thread: "thread-ok", usd: 2m, finishedAt: Now.AddHours(-1));
        var unmeasured = Receipt("completed", thread: "thread-gone", usd: null, finishedAt: Now.AddHours(-1));
        var usdNull = Receipt("completed", thread: "thread-null", usd: null, finishedAt: Now.AddHours(-1));
        usdNull["judgeUsage"]!["measured"] = true;
        var alpha = Db(
            "alpha",
            Judgment("7001", 1, unpriced),
            Judgment("7002", 1, unreadable),
            Judgment("7003", 1, complete),
            Judgment("7004", 1, unmeasured),
            Judgment("7005", 1, usdNull)
        );
        var host = new FakeHttpMessageHandler()
            .OnJson(HttpMethod.Get, "thread-unpriced/usage", """{"totalTokens":10,"completeness":0}""")
            .OnJson(HttpMethod.Get, "thread-down/usage", "{}", HttpStatusCode.InternalServerError)
            .OnJson(HttpMethod.Get, "thread-gone/usage", "{}", HttpStatusCode.NotFound);

        var (scan, _) = await PlanAsync(host, [], Target("alpha", alpha));

        scan.Guard.Spent24h.Should().Be(22m);
        host.CountRequests("thread-ok/usage").Should().Be(0);
        // Each cost charged at its cap says so, and none is called a lower bound.
        scan.NeedsYou.Should()
            .HaveCount(4)
            .And.OnlyContain(note =>
                note.What == "cost not known; until it is, the daily cap counts its cost cap ($5.00)"
            );
        JudgeBatch
            .NeedsYouLines(scan.NeedsYou)
            .Should()
            .ContainSingle()
            .Which.Should()
            .StartWith("4 × cost not known; until it is, the daily cap counts its cost cap ($5.00): ")
            .And.EndWith(" and 1 more");
    }

    [Fact]
    public void Judgment_keys_parse_one_way_for_runner_and_batch_F1()
    {
        var prefix = $"reviewd/judge-run/{RepoKey}/7001/";

        JudgeBatch
            .Judgments([(prefix + "10", """{"n":10}"""), (prefix + "2", """{"n":2}""")], prefix)
            .Keys.Should()
            .Equal(2, 10);
        var stray = () => JudgeBatch.Judgments([(prefix + "2.bak", "{}")], prefix);
        stray.Should().Throw<InvalidOperationException>().WithMessage($"row {prefix}2.bak has no judgment number*");
    }

    // ---- batch contracts

    [Fact]
    public void The_classification_table()
    {
        JudgeBatch.Pick Of(
            JsonObject[] judgments,
            string? lifecycle = "Merged",
            double mergedDaysAgo = 1,
            double? skippedHoursAgo = null,
            bool inFlight = false
        ) =>
            JudgeBatch.Classify(
                new JudgeBatch.PrState(
                    Target("alpha", "alpha.db"),
                    Repo,
                    RepoKey,
                    "7001",
                    judgments,
                    lifecycle,
                    Now.AddDays(-mergedDaysAgo),
                    skippedHoursAgo is { } hours ? Now.AddHours(-hours) : null
                ),
                inFlight,
                Now,
                Settings
            );
        static string Row(JudgeBatch.Pick pick) => $"{pick.Kind} {pick.Reason}{(pick.Stored ? " (stored)" : "")}";
        JsonObject Stage(string stage, string? thread = "thread-1", double finishedHoursAgo = 1)
        {
            var receipt = Receipt(stage, thread, finishedAt: Now.AddHours(-finishedHoursAgo));
            receipt["n"] = 1;
            return receipt;
        }

        // Never judged: the snapshot and a fresh skip marker decide.
        Row(Of([])).Should().Be("Start new");
        Row(Of([], mergedDaysAgo: 8)).Should().Be("Skip too-old");
        Row(Of([], lifecycle: null)).Should().Be("Skip not-in-snapshot");
        Row(Of([], lifecycle: "Active")).Should().Be("Skip active");
        Row(Of([], skippedHoursAgo: 23)).Should().Be("Skip skip-cooling");
        Row(Of([], skippedHoursAgo: 25)).Should().Be("Start new");

        // Unfinished: only a judge the host accepted skips the guards; another prompt or no cap needs a person.
        Row(Of([Stage("judge-running")])).Should().Be("Resume resume judgment 1 from the judging step (stored)");
        Row(Of([Stage("send-pending")])).Should().Be("Start resume judgment 1 from the sending step (stored)");
        Row(Of([Stage("send-pending")], inFlight: true))
            .Should()
            .Be("Resume resume judgment 1 from the sending step (stored)");
        Row(Of([Stage("inputs-written", thread: null)])).Should().StartWith("Start resume judgment 1");
        var otherPrompt = Stage("judge-running");
        otherPrompt["identity"]!["promptTemplateSha256"] = "old-prompt";
        Row(Of([otherPrompt]))
            .Should()
            .Be(
                "NeedsYou judgment 1 stopped in the judging step under another prompt; resume it by hand with the original prompt"
            );
        var noCap = Stage("judge-running");
        noCap.Remove("caps");
        Row(Of([noCap]))
            .Should()
            .Be("NeedsYou judgment 1 stopped in the judging step with no stored cap; resume it by hand");
        // While such a run is live it is only "Running now".
        Row(Of([otherPrompt], inFlight: true)).Should().Be("Skip running");
        Row(Of([noCap], inFlight: true)).Should().Be("Skip running");

        // Capped and unsettled: post-check within 24 h of the freeze, then a person.
        var capped = Stage("capped");
        capped["descendantsSettled"] = false;
        capped["frozenAt"] = Now.AddHours(-2);
        Row(Of([capped])).Should().Be("PostCheck post-check capped judgment 1 (stored)");
        capped["frozenAt"] = Now.AddHours(-25);
        Of([capped]).Kind.Should().Be(JudgeBatch.Kind.NeedsYou);

        // Finished: judged, whatever the stage; failed-precondition retries once a day, twice is a person.
        foreach (var stage in new[] { "completed", "completed-draft", "failed", "failed-no-output", "ado-changed" })
            Row(Of([Stage(stage)])).Should().Be("Skip judged", stage);
        Row(Of([Stage("failed-precondition", finishedHoursAgo: 25)]))
            .Should()
            .Be("Start retry judgment 1, which could not start");
        Row(Of([Stage("failed-precondition", finishedHoursAgo: 23)])).Should().Be("Skip precondition-cooling");
        Row(
                Of([
                    Stage("failed-precondition", finishedHoursAgo: 48),
                    Stage("failed-precondition", finishedHoursAgo: 25),
                ])
            )
            .Should()
            .Be(
                "NeedsYou 2 judgments could not start because the host could not create the judge conversation "
                    + "(details: journalctl --user -u 'reviewd-judge*'); fix that, then judge run by hand"
            );
    }

    [Fact]
    public void The_summary_says_stages_in_words_and_a_cut_table_says_how_many_more_W_2_W_4()
    {
        var scan = new JudgeBatch.Scan();
        for (var i = 1; i <= 22; i++)
        {
            var receipt = Receipt(i == 22 ? "failed-precondition" : "completed", finishedAt: Now.AddHours(-1));
            receipt["n"] = i;
            scan.Receipts.Add(("Nova !7001", receipt));
        }

        var lines = JudgeBatch
            .Summary(scan, [], [], [], Settings, execute: true, "ok", 0, Now)
            .ReplaceLineEndings("\n")
            .Split('\n');

        lines
            .Should()
            .Contain(line => line.StartsWith("- Judgments in the last 24 h: 22 (21 done, 1 could not start)."));
        lines.Should().Contain("| Nova !7001 #1 | done | $1.00 | 0 |");
        lines.Should().Contain("| … and 2 more | | | |", "the table shows 20 rows and says how many it left out");
    }

    // ---- refused sign-in, the 24 h table and wording

    [Fact]
    public async Task A_live_judge_under_the_current_prompt_is_only_running_now_but_stays_planned_Q_R4_1()
    {
        var alpha = Db(
            "alpha",
            Review("7001"),
            Snapshot(("7001", "Merged", Now.AddDays(-1))),
            Judgment("7001", 1, Receipt("judge-running", thread: "thread-live"))
        );
        const string Usage = """{"preferredCostMicros":1000000,"completeness":0}""";
        var live = new FakeHttpMessageHandler()
            .OnJson(HttpMethod.Get, "thread-live/run-state", Active)
            .OnJson(HttpMethod.Get, "thread-live/usage", Usage);

        var (scan, plan) = await PlanAsync(live, [], Target("alpha", alpha));

        // The resume stays planned: it re-attaches when the host still runs the judge but its runner process died.
        plan.Should().ContainSingle().Which.Pick.Kind.Should().Be(JudgeBatch.Kind.Resume);
        plan.Single().Wait.Should().BeNull();
        scan.Counts.Should().BeEquivalentTo(new Dictionary<string, int> { ["running"] = 1 });
        JudgeBatch
            .Summary(scan, plan, [], [], Settings, execute: false, "ok", 0, Now)
            .Should()
            .Contain("- PRs: 1 running now.")
            .And.Contain("Running now: Nova !7001.")
            .And.Contain("- Nova !7001: running now; the batch re-attaches only if its process stops")
            .And.NotContain("to resume")
            .And.NotContain("would run");

        // Once the host says it is idle, it is an ordinary resume.
        var idle = new FakeHttpMessageHandler()
            .OnJson(HttpMethod.Get, "thread-live/run-state", Idle)
            .OnJson(HttpMethod.Get, "thread-live/usage", Usage);
        var (after, afterPlan) = await PlanAsync(idle, [], Target("alpha", alpha));
        after.Counts.Should().BeEquivalentTo(new Dictionary<string, int> { ["Resume"] = 1 });
        JudgeBatch
            .Summary(after, afterPlan, [], [], Settings, execute: false, "ok", 0, Now)
            .Should()
            .Contain("- PRs: 1 to resume.")
            .And.Contain("- Nova !7001: resume (resume judgment 1 from the judging step); would run");
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task A_host_that_refuses_the_sign_in_is_named_once_and_the_status_is_never_ok_A_13(
        HttpStatusCode refusal
    )
    {
        // A host that refuses the batch's credentials refuses every read.
        var host = new FakeHttpMessageHandler().On(_ => true, _ => new HttpResponseMessage(refusal));

        var (scan, plan) = await PlanAsync(host, [], Target("alpha", ThreeHostReadsDb()));

        var code = (int)refusal;
        scan.NeedsYou.Select(note => note.ToString())
            .Should()
            .Equal(
                $"the host: refused the batch's sign-in (HTTP {code}), so judges count as running and costs at their "
                    + "caps; check the batch's credentials (Nova's bin/env.sh loads them)"
            );
        scan.InFlight.Should().Equal("Nova !7001", "Nova !7002 (capped judge still settling)");
        scan.Guard.Spent24h.Should().Be(15m, "each cost the host would not give counts at its $5 cap");
        JudgeBatch
            .Conclude(scan, plan, Settings, stopFile: null, latched: false, (0, "", null))
            .Should()
            .Be((0, $"the host refused the batch's sign-in (HTTP {code}); see Needs you", null));
    }

    [Fact]
    public async Task A_host_error_that_is_not_a_refusal_still_names_each_judge_A_13()
    {
        var host = new FakeHttpMessageHandler().On(_ => true, _ => new HttpResponseMessage(HttpStatusCode.BadGateway));

        var (scan, plan) = await PlanAsync(host, [], Target("alpha", ThreeHostReadsDb()));

        scan.NeedsYou.Select(note => note.ToString())
            .Should()
            .Equal(
                "Nova !7001: the host did not say whether its judge is still running; treated as running",
                "Nova !7002: the host did not say whether its capped judge has settled; treated as running",
                "Nova !7001 #1: cost not known; until it is, the daily cap counts its cost cap ($5.00)",
                "Nova !7002 #1: cost not known; until it is, the daily cap counts its cost cap ($5.00)",
                "Nova !7003 #1: cost not known; until it is, the daily cap counts its cost cap ($5.00)"
            );
        JudgeBatch
            .Conclude(scan, plan, Settings, stopFile: null, latched: false, (0, "", null))
            .Status.Should()
            .Be("ok");
    }

    // A judge in its judging step, a capped judge that may still settle, and a finished judge whose cost may still
    // grow: each needs a host read.
    private string ThreeHostReadsDb()
    {
        var capped = Receipt("capped", thread: "thread-capped");
        capped["frozenAt"] = Now.AddHours(-1);
        capped["descendantsSettled"] = false;
        capped["ado"] = new JsonObject { ["compare"] = "unchanged" };
        var growing = Receipt("completed", thread: "thread-done", finishedAt: Now.AddHours(-1));
        growing["judgeUsage"]!["completeness"] = 0;
        return Db(
            "alpha",
            Review("7001"),
            Review("7002"),
            Review("7003"),
            Judgment("7001", 1, Receipt("judge-running", thread: "thread-running")),
            Judgment("7002", 1, capped),
            Judgment("7003", 1, growing)
        );
    }

    [Fact]
    public void The_cut_table_shows_the_newest_judgments_first_and_a_judgment_that_never_started_cost_nothing_A_14()
    {
        var scan = new JudgeBatch.Scan();
        // Scan order is by PR; here the higher the PR, the later its judgment finished.
        for (var i = 1; i <= 22; i++)
        {
            var receipt = Receipt("completed", thread: $"thread-{i}", finishedAt: Now.AddMinutes(-60 + i));
            receipt["n"] = 1;
            scan.Receipts.Add(($"Nova !{7000 + i}", receipt));
        }
        // The newest: it could not start, so it has no judge thread and no usage reading.
        var neverStarted = Receipt("failed-precondition", finishedAt: Now);
        neverStarted.Remove("judgeUsage");
        neverStarted["n"] = 2;
        scan.Receipts.Add(("Nova !7001", neverStarted));

        var rows = JudgeBatch
            .Summary(scan, [], [], [], Settings, execute: true, "ok", 0, Now)
            .ReplaceLineEndings("\n")
            .Split('\n')
            .Where(line =>
                line.StartsWith("| ", StringComparison.Ordinal) && !line.StartsWith("| PR ", StringComparison.Ordinal)
            )
            .ToList();

        rows.Should().HaveCount(21);
        rows[0].Should().Be("| Nova !7001 #2 | could not start | $0.00 | 0 |");
        rows[1].Should().StartWith("| Nova !7022 #1 |");
        rows[19].Should().StartWith("| Nova !7004 #1 |");
        rows[20].Should().Be("| … and 3 more | | | |", "the three oldest are cut");
    }

    [Fact]
    public void The_status_says_why_judging_is_paused_and_how_to_undo_it_never_ok_beside_paused_W_3()
    {
        var stop = Path.Combine(_dir, "STOPPED");
        File.WriteAllText(stop, "2026-10-22T22:17Z: 2 judgments in a row failed\n");
        JudgeBatch
            .Conclude(new JudgeBatch.Scan(), [], Settings, stop, latched: true, (0, "", null))
            .Status.Should()
            .Be(
                "paused (2026-10-22T22:17Z: 2 judgments in a row failed). Judges already running still finish. "
                    + $"To start new judges again, run: rm {stop}"
            );
        File.WriteAllText(stop, "");
        JudgeBatch
            .Conclude(new JudgeBatch.Scan(), [], Settings, stop, latched: true, (0, "", null))
            .Status.Should()
            .Be($"paused. Judges already running still finish. To start new judges again, run: rm {stop}");

        // An unusable install pauses new judges: the status says "paused", never "ok" beside it.
        var skipped = new JudgeBatch.Scan();
        skipped.Skipped.Add(("viva-pulse", "its folder is missing (check JUDGE_INSTALLS)"));
        JudgeBatch
            .Conclude(skipped, [], Settings, stopFile: null, latched: false, (0, "", null))
            .Status.Should()
            .Be("paused: 1 install unusable (see Needs you); judges already running still finish");

        // The average guard names a short cause; the tick puts it in the alert title.
        var costly = new JudgeBatch.Scan { Guard = new(0m, 4.5m, 10) };
        var (exit, status, cause) = JudgeBatch.Conclude(costly, [], Settings, null, latched: false, (0, "", null));
        (exit, status, cause)
            .Should()
            .Be(
                (
                    5,
                    "paused: the average cost $4.50 over the last 10 judgments is above the $4.00 limit",
                    "average cost too high"
                )
            );
        var json = JudgeBatch
            .Summary(costly, [], [], [], Settings, execute: true, status, exit, Now, cause)
            .TrimEnd()
            .Split('\n')[^1];
        JsonNode.Parse(json)!["cause"]!.GetValue<string>().Should().Be("average cost too high");
    }

    // "ok" sat beside a daily cap that lets no new judge start, and a
    // judge in flight (here: the host down, each unknown cost counted at its cap) hid that cap wait entirely.
    [Fact]
    public void The_status_is_never_ok_while_the_daily_cap_lets_no_new_judge_start()
    {
        static JudgeBatch.Pick Start(string pr) =>
            new(
                new(Target("alpha", "alpha.db"), Repo, RepoKey, pr, [], "Merged", Now, null),
                JudgeBatch.Kind.Start,
                ""
            );
        const string noRoom = "waiting: no room under the daily cap for another new judge";

        var underCap = new JudgeBatch.Scan { Guard = new(28m, null, 0) };
        JudgeBatch
            .Conclude(underCap, [(Start("1"), "cap")], Settings, null, latched: false, (0, "", null))
            .Status.Should()
            .Be(noRoom);

        var overCap = new JudgeBatch.Scan { Guard = new(48.73m, null, 0) };
        JudgeBatch
            .Conclude(
                overCap,
                [(Start("1"), "waits: judge in flight on Nova !1")],
                Settings,
                null,
                latched: false,
                (0, "", null)
            )
            .Status.Should()
            .Be(noRoom);

        // Room for one more is not a stop: "ok" stays.
        JudgeBatch
            .Conclude(
                underCap,
                [(Start("1"), null), (Start("2"), "cap")],
                Settings,
                null,
                latched: false,
                (0, "", null)
            )
            .Status.Should()
            .Be("ok · the daily cap leaves room for 1 more new judge");
    }

    // A judge whose input failed while its sub-agents still ran: they still spend and may still touch ADO, so new
    // starts wait for them, and once they are quiet the next batch repeats the post-check.
    [Fact]
    public async Task A_failed_judgment_whose_sub_agents_still_run_holds_new_starts_until_they_are_quiet()
    {
        var failed = Receipt(
            "failed",
            thread: "thread-failed",
            startedAt: Now.AddHours(-2),
            finishedAt: Now.AddHours(-1)
        );
        failed["descendantsSettled"] = false;
        var db = Db(
            "alpha",
            Review("7001"),
            Review("7002"),
            Snapshot(("7001", "Merged", Now.AddDays(-1)), ("7002", "Merged", Now.AddDays(-1))),
            Judgment("7001", 1, failed)
        );
        async Task<(JudgeBatch.Scan Scan, List<string> Rows)> PlanWith(string subAgent)
        {
            var host = new FakeHttpMessageHandler()
                .OnJson(HttpMethod.Get, "thread-failed/run-state", Idle)
                .OnJson(HttpMethod.Get, "thread-failed/subagents?recursive=true", Tree(subAgent))
                .OnJson(HttpMethod.Get, "thread-failed/usage", """{"preferredCostMicros":1000000,"completeness":1}""");
            var (scan, plan) = await PlanAsync(host, [], Target("alpha", db));
            return (scan, [.. plan.Select(entry => $"{entry.Pick.Kind} {entry.Pick.Pr.Pr}: {entry.Wait ?? "runs"}")]);
        }

        var (running, held) = await PlanWith("running");
        running.InFlight.Should().Equal("Nova !7001 (failed judge still settling)");
        held.Should()
            .Equal(
                "PostCheck 7001: runs",
                "Start 7002: waits: judge in flight on Nova !7001 (failed judge still settling)"
            );

        var (quiet, free) = await PlanWith("completed");
        quiet.InFlight.Should().BeEmpty();
        free.Should().Equal("PostCheck 7001: runs", "Start 7002: runs");
    }

    // A judge whose send never landed leaves a stored start: a thread, no usage. Its own possible spend counts once in
    // the day cap, and a thread the host holds no usage for (404) and that has no reading has spent nothing.
    [Fact]
    public async Task A_stored_start_whose_send_never_landed_counts_once_against_the_day_cap()
    {
        var defaults = Settings with { MaxUsd = 15m };
        var pending = Receipt("send-pending", thread: "thread-pending", startedAt: Now.AddMinutes(-90));
        pending.Remove("judgeUsage");
        pending["caps"]!["maxUsd"] = 15m;
        var db = Db(
            "alpha",
            Review("7001"),
            Review("7002"),
            Snapshot(("7001", "Merged", Now.AddDays(-1)), ("7002", "Merged", Now.AddDays(-2))),
            Judgment("7001", 1, pending),
            Judgment("7002", 1, Receipt("completed", thread: "thread-done", usd: 2m, finishedAt: Now.AddHours(-1)))
        );
        async Task<(JudgeBatch.Scan Scan, string? Wait)> PlanWith(HttpStatusCode usage, JudgeBatch.Settings settings)
        {
            var host = new FakeHttpMessageHandler()
                .OnJson(HttpMethod.Get, "thread-pending/run-state", Idle)
                .OnJson(HttpMethod.Get, "thread-pending/usage", "{}", usage);
            using var http = new HttpClient(host) { BaseAddress = new Uri("http://localhost/") };
            var client = new LmStreamingS2SClient(http, null, null, null);
            var scan = await JudgeBatch.ScanAsync([Target("alpha", db)], [], client, settings, Now, default);
            var plan = JudgeBatch.Plan(scan, [], latched: false, 0, int.MaxValue, settings, Now);
            return (scan, plan.Single(entry => entry.Pick.Kind == JudgeBatch.Kind.Start).Wait);
        }

        var (unsent, unsentWait) = await PlanWith(HttpStatusCode.NotFound, defaults);
        unsent.Guard.Spent24h.Should().Be(2m, "a thread with no usage and no reading has spent nothing");
        unsent.NeedsYou.Should().BeEmpty();
        unsentWait.Should().BeNull();

        var (unknown, unknownWait) = await PlanWith(HttpStatusCode.BadGateway, defaults);
        unknown.Guard.Spent24h.Should().Be(17m, "an unread cost counts at its $15 cap");
        unknownWait.Should().BeNull("the start's own $15 is already counted; it is not charged again");

        var (_, tightWait) = await PlanWith(HttpStatusCode.BadGateway, defaults with { MaxUsdPerDay = 20m });
        tightWait.Should().BeNull("$2 + its own $15 fits a $20 day cap");
    }

    // A judge in flight holds every start, which hid the cap: $28.78 spent plus the next $15 judge is over $30.
    [Fact]
    public void The_status_is_never_ok_when_the_next_start_would_not_fit_the_daily_cap()
    {
        var defaults = Settings with { MaxUsd = 15m };
        var start = new JudgeBatch.Pick(
            new(Target("alpha", "alpha.db"), Repo, RepoKey, "2", [], "Merged", Now, null),
            JudgeBatch.Kind.Start,
            "new"
        );
        List<(JudgeBatch.Pick, string?)> plan = [(start, "waits: judge in flight on Nova !1")];
        string StatusAt(decimal spent) =>
            JudgeBatch
                .Conclude(
                    new JudgeBatch.Scan { Guard = new(spent, null, 0) },
                    plan,
                    defaults,
                    null,
                    latched: false,
                    (0, "", null)
                )
                .Status;

        StatusAt(28.78m).Should().Be("waiting: no room under the daily cap for another new judge");
        StatusAt(10m).Should().Be("ok", "the next judge fits once the one in flight ends");
    }

    [Fact]
    public void The_summary_says_miss_categories_and_failures_in_plain_words()
    {
        var scan = new JudgeBatch.Scan();
        var judged = Receipt("completed", thread: "thread-1", finishedAt: Now.AddHours(-1));
        judged["n"] = 1;
        judged["judgment"] = JsonNode.Parse(
            """{"gapSummary":{"knowledge":5,"pipeline-filter":5,"hallucination":2,"process-generic":1,"process-repo":4,"no-lesson":0,"proposed":[]}}"""
        );
        scan.Receipts.Add(("Nova !7001", judged));
        List<JsonObject> actions =
        [
            new()
            {
                ["pr"] = "Nova !7002",
                ["kind"] = "Start",
                ["outcome"] = "Failed",
            },
        ];

        var summary = JudgeBatch.Summary(scan, [], actions, [], Settings, execute: true, "ok", 0, Now);

        const string Misses =
            "missing system knowledge 5, review method (any repo) 1, review method (this repo) 4, "
            + "found but dropped by our pipeline 5, posted something with no basis 2";
        summary.Should().Contain($"Missed issues: {Misses}.").And.Contain($"- Missed issues in 7 days: {Misses}.");
        summary.Should().NotContain("pipeline-filter").And.NotContain("process-repo").And.NotContain("hallucination");
        summary.Should().Contain("- Nova !7002: start, failed (details: journalctl --user -u 'reviewd-judge*')");
    }

    [Fact]
    public async Task A_failed_judgment_says_what_to_do_next_and_an_unreadable_install_names_the_log_W_2()
    {
        var failed = Receipt("failed", thread: "thread-f1", finishedAt: Now.AddHours(-2));
        failed["error"] = "Judge input x ended Failed; not success.";
        var alpha = Db(
            "alpha",
            Review("7001"),
            Review("7002"),
            Review("7003"),
            Judgment("7001", 1, failed),
            Judgment("7002", 1, Receipt("failed", thread: "thread-f2", finishedAt: Now.AddHours(-3))),
            Judgment("7003", 1, Receipt("failed-no-output", thread: "thread-f3", finishedAt: Now.AddHours(-4)))
        );
        // A judge row whose stage is not text: the install cannot be read for a reason the batch has no words for.
        var beta = Db("beta", ($"reviewd/judge-run/{RepoKey}/8001/1", """{"n":1,"stage":5}"""));

        var (scan, _) = await PlanAsync(new FakeHttpMessageHandler(), [], Target("alpha", alpha), Target("beta", beta));

        const string Next =
            "and is not retried; see why in journalctl --user -u 'reviewd-judge*', then judge run --rejudge to judge it again";
        JudgeBatch
            .NeedsYouLines(scan.NeedsYou)
            .Should()
            .BeEquivalentTo(
                "install beta: unusable because it could not be read (details: journalctl --user -u 'reviewd-judge*')",
                $"2 × last judgment failed {Next}: Nova !7001, Nova !7002",
                $"Nova !7003: last judgment failed with no output {Next}"
            );
    }

    // A failed judgment whose sub-agents had not settled is re-checked next run, whether they still run or not, so
    // Needs you says so and never "not retried". Once the re-check settles it, the line says how to judge it again.
    [Fact]
    public async Task A_failed_judgment_due_a_re_check_says_re_check_pending_until_it_settles()
    {
        var unsettled = FailedJudgmentDb("unsettled", settled: false);
        var down = new FakeHttpMessageHandler().On(_ => true, _ => new HttpResponseMessage(HttpStatusCode.BadGateway));
        var quiet = new FakeHttpMessageHandler()
            .OnJson(HttpMethod.Get, "thread-failed/run-state", Idle)
            .OnJson(HttpMethod.Get, "thread-failed/subagents?recursive=true", Tree("completed"))
            .OnJson(HttpMethod.Get, "thread-failed/usage", """{"preferredCostMicros":400000,"completeness":1}""");
        foreach (var host in new[] { down, quiet })
        {
            (await SummaryLinesAsync(host, unsettled))
                .Should()
                .Contain("- Nova !7001: last judgment failed while its sub-agents still ran; re-check pending")
                .And.Contain("- Nova !7001: re-check (post-check failed judgment 1); would run")
                .And.NotContain(line => line.Contains("not retried"));
        }

        (await SummaryLinesAsync(new FakeHttpMessageHandler(), FailedJudgmentDb("settled", settled: true)))
            .Should()
            .Contain(
                "- Nova !7001: last judgment failed and is not retried; see why in journalctl --user -u 'reviewd-judge*', "
                    + "then judge run --rejudge to judge it again"
            )
            .And.NotContain(line => line.Contains("re-check"));
    }

    // Still unsettled 24 h after it started, a failed judgment is not re-checked any more. judge run repeats its
    // settle and post-check before any --rejudge, so the advice says judge run first (--rejudge alone wasted a run).
    [Fact]
    public async Task A_failed_judgment_unsettled_after_24_h_says_judge_run_before_rejudge()
    {
        var stale = FailedJudgmentDb("stale", settled: false, startedHoursAgo: 30);

        (await SummaryLinesAsync(new FakeHttpMessageHandler(), stale))
            .Should()
            .Contain(
                "- Nova !7001: last judgment failed and is not retried; see why in journalctl --user -u 'reviewd-judge*', "
                    + "then judge run until its sub-agents settle, then judge run --rejudge to judge it again"
            )
            .And.NotContain(line => line.Contains("re-check pending"));
    }

    // The 24 h table shows what a judgment has spent so far. While its cost is not known, the daily cap counts its
    // cost cap instead, and the note says which number counts.
    [Fact]
    public async Task An_unknown_cost_says_the_daily_cap_counts_its_cost_cap_until_it_is_known()
    {
        var down = new FakeHttpMessageHandler().On(_ => true, _ => new HttpResponseMessage(HttpStatusCode.BadGateway));

        var lines = await SummaryLinesAsync(down, FailedJudgmentDb("alpha", settled: false));

        lines
            .Should()
            .Contain("| Nova !7001 #1 | failed | $0.40 | 0 |")
            .And.Contain("- Spent in the last 24 h: $15.00 of the $30.00 daily cap.")
            .And.Contain("- Nova !7001 #1: cost not known; until it is, the daily cap counts its cost cap ($15.00)");
    }

    [Fact]
    public void Guards_count_a_rolling_day_and_average_the_last_ten_finished_judgments()
    {
        static JudgeBatch.Spend Spend(
            decimal usd,
            double? finishedHoursAgo,
            double startedHoursAgo,
            string? thread = "t"
        ) =>
            new(
                "x",
                thread,
                Now.AddHours(-startedHoursAgo),
                finishedHoursAgo is { } hours ? Now.AddHours(-hours) : null,
                usd,
                LowerBound: false,
                Cap: 5m,
                Unknown: false
            );

        var day = JudgeBatch.Guards(
            [
                Spend(20m, finishedHoursAgo: 23, startedHoursAgo: 26),
                Spend(7m, finishedHoursAgo: 25, startedHoursAgo: 26),
                Spend(3m, finishedHoursAgo: null, startedHoursAgo: 30),
                Spend(100m, finishedHoursAgo: 1, startedHoursAgo: 2, thread: null),
            ],
            Now
        );
        day.Spent24h.Should().Be(23m, "finished within 24 h or still running counts; no thread means no spend");
        day.Avg10.Should().BeNull("the average is off below 5 finished judgments");

        List<JudgeBatch.Spend> eleven = [Spend(50m, finishedHoursAgo: 100, startedHoursAgo: 101)];
        eleven.AddRange(Enumerable.Range(1, 10).Select(i => Spend(1m, finishedHoursAgo: i, startedHoursAgo: i + 0.5)));
        var guard = JudgeBatch.Guards(eleven, Now);
        guard.Avg10.Should().Be(1m, "only the last 10 by finish time count");
        guard.AvgCount.Should().Be(10);
    }

    [Fact]
    public void The_plan_resumes_and_post_checks_first_then_starts_take_turns_inside_the_guards()
    {
        JudgeBatch.Pick Pick(string target, string pr, JudgeBatch.Kind kind, double hoursAgo)
        {
            var receipt = Receipt("judge-running", startedAt: Now.AddHours(-hoursAgo));
            return new(
                new(
                    Target(target, target + ".db"),
                    Repo,
                    RepoKey,
                    pr,
                    kind == JudgeBatch.Kind.Start ? [] : [receipt],
                    "Merged",
                    Now.AddHours(-hoursAgo),
                    null
                ),
                kind,
                kind.ToString(),
                Stored: kind != JudgeBatch.Kind.Start
            );
        }
        JudgeBatch.Scan Scan(decimal spent = 0m, decimal? avg = null)
        {
            var scan = new JudgeBatch.Scan { Guard = new(spent, avg, avg is null ? 0 : 5) };
            scan.Picks.AddRange([
                Pick("beta", "1", JudgeBatch.Kind.Start, 5),
                Pick("beta", "2", JudgeBatch.Kind.Start, 1),
                Pick("alpha", "3", JudgeBatch.Kind.Start, 2),
                Pick("alpha", "4", JudgeBatch.Kind.PostCheck, 9),
                Pick("beta", "5", JudgeBatch.Kind.Resume, 3),
                Pick("alpha", "6", JudgeBatch.Kind.Resume, 8),
            ]);
            scan.LastStart["alpha"] = Now.AddHours(-1);
            scan.LastStart["beta"] = null;
            return scan;
        }
        static List<string> Rows(List<(JudgeBatch.Pick Pick, string? Wait)> plan) =>
            [.. plan.Select(entry => $"{entry.Pick.Kind} {entry.Pick.Pr.Pr}: {entry.Wait ?? "runs"}")];

        // Resumes oldest first, post-checks, then starts: beta (never judged) first, newest merge first, taking turns.
        Rows(JudgeBatch.Plan(Scan(), [], latched: false, 0, int.MaxValue, Settings, Now))
            .Should()
            .Equal(
                "Resume 6: runs",
                "Resume 5: runs",
                "PostCheck 4: runs",
                "Start 2: runs",
                "Start 3: runs",
                "Start 1: runs"
            );

        // Day cap projected at each run's $5 cap: $20 spent leaves room for 2 of 3.
        Rows(JudgeBatch.Plan(Scan(spent: 20m), [], latched: false, 0, int.MaxValue, Settings, Now))
            .Skip(3)
            .Should()
            .Equal("Start 2: runs", "Start 3: runs", "Start 1: cap");
        Rows(JudgeBatch.Plan(Scan(), [], latched: false, 0, limit: 1, Settings, Now))
            .Skip(3)
            .Should()
            .Equal("Start 2: runs", "Start 3: limit", "Start 1: limit");

        // Each gate stops starts only; resumes and post-checks still run.
        var inFlight = Scan();
        inFlight.InFlight.Add("Nova !9");
        foreach (
            var (plan, wait) in new[]
            {
                (
                    JudgeBatch.Plan(inFlight, [], latched: false, 0, int.MaxValue, Settings, Now),
                    "waits: judge in flight on Nova !9"
                ),
                (JudgeBatch.Plan(Scan(), [], latched: true, 0, int.MaxValue, Settings, Now), "latched"),
                (
                    JudgeBatch.Plan(Scan(avg: 4.01m), [], latched: false, 0, int.MaxValue, Settings, Now),
                    "average guard"
                ),
            }
        )
        {
            Rows(plan).Take(3).Should().AllSatisfy(row => row.Should().EndWith(": runs"));
            plan.Skip(3).Should().AllSatisfy(entry => entry.Wait.Should().Be(wait));
        }

        // A pick this run already tried is not planned again.
        var tried = Scan();
        JudgeBatch
            .Plan(tried, [tried.Picks[4].Pr.Id], latched: false, 0, int.MaxValue, Settings, Now)
            .Should()
            .NotContain(entry => entry.Pick.Pr.Pr == "5");
    }

    // Two lanes share one install as shards 0/2 and 1/2. The even lane's judge on 7002 is in flight and has spent
    // $4: the odd lane neither waits for it nor charges its cost (it reads nothing of it on the host), and plans only
    // the odd PRs, newest merge first. The even lane still waits for its own judge before 7004.
    [Fact]
    public async Task A_shard_lane_takes_only_its_own_PRs_and_waits_only_for_its_own_judges()
    {
        var alpha = Db(
            "alpha",
            Review("7001"),
            Review("7002"),
            Review("7003"),
            Review("7004"),
            Snapshot(
                ("7001", "Merged", Now.AddDays(-4)),
                ("7002", "Merged", Now.AddDays(-3)),
                ("7003", "Merged", Now.AddDays(-2)),
                ("7004", "Merged", Now.AddDays(-1))
            ),
            Judgment("7002", 1, Receipt("judge-running", thread: "thread-even", usd: 4m)),
            // Two days old: 7004 may start, and only the even lane's Needs you mentions the skip.
            ("reviewd/judge-skip/" + RepoKey + "/7004", new JsonObject { ["at"] = Now.AddDays(-2) }.ToJsonString())
        );
        var quiet = new FakeHttpMessageHandler();

        var (odd, oddPlan) = await PlanAsync(quiet, [], Settings with { Shard = new(1, 2) }, Target("alpha", alpha));

        odd.InFlight.Should().BeEmpty();
        odd.Guard.Spent24h.Should().Be(0m);
        odd.NeedsYou.Should().BeEmpty();
        quiet.Requests.Should().BeEmpty();
        oddPlan
            .Select(entry => (entry.Pick.Pr.Label, entry.Pick.Kind, entry.Wait))
            .Should()
            .Equal(("Nova !7003", JudgeBatch.Kind.Start, null), ("Nova !7001", JudgeBatch.Kind.Start, null));

        var host = new FakeHttpMessageHandler().OnJson(HttpMethod.Get, "thread-even/run-state", Active);
        var (even, evenPlan) = await PlanAsync(host, [], Settings with { Shard = new(0, 2) }, Target("alpha", alpha));

        even.InFlight.Should().Equal("Nova !7002");
        even.NeedsYou.Select(note => note.ToString()).Should().ContainMatch("*Nova !7004*not completed*");
        evenPlan
            .Single(entry => entry.Pick.Kind == JudgeBatch.Kind.Start)
            .Should()
            .Match<(JudgeBatch.Pick Pick, string? Wait)>(entry =>
                entry.Pick.Pr.Label == "Nova !7004" && entry.Wait == "waits: judge in flight on Nova !7002"
            );
    }

    // The tick passes JUDGE_SHARD as --shard: a preview of the CLI entry plans only that shard's PRs. No PR here has a
    // judgment, so the preview makes no host read.
    [Fact]
    public async Task Judge_batch_with_shard_plans_only_its_own_PRs()
    {
        var alpha = Db(
            "alpha",
            Review("7001"),
            Review("7002"),
            Snapshot(("7001", "Merged", Now.AddDays(-2)), ("7002", "Merged", Now.AddDays(-1)))
        );
        var targets = Path.Combine(_dir, "targets.tsv");
        await File.WriteAllTextAsync(targets, $"alpha\t{alpha}\tsession-1\t{Workspace}\n");
        using var config = new ReviewdConfig(new ConfigurationBuilder().Build());
        var (stdout, stderr) = (Console.Out, Console.Error);
        using var output = new StringWriter();
        Console.SetOut(output);
        Console.SetError(TextWriter.Null);
        int exit;
        try
        {
            exit = await JudgeBatch.RunAsync(
                CliOptions.Parse(["judge", "batch", "--targets", targets, "--shard", "1/2"]),
                config,
                CancellationToken.None
            );
        }
        finally
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
        }

        exit.Should().Be(0);
        JsonNode.Parse(output.ToString().TrimEnd().Split('\n')[^1])!["plan"]!
            .AsArray()
            .Select(row => row!["pr"]!.GetValue<string>())
            .Should()
            .Equal("Nova !7001");
    }

    // The judge refuses a verified review whose receipt has no artifact commit (eleven Nova reviews have none). The
    // scan counts it and never picks it; picked, it failed every run and two in a row stopped the lane.
    [Theory]
    [InlineData(null)]
    [InlineData("0123abc")]
    [InlineData("0123456789abcdef0123456789abcdef0123456")]
    [InlineData("0123456789abcdef0123456789abcdef012345678")]
    public async Task A_review_with_no_verified_artifact_commit_is_counted_and_never_picked(string? artifactCommit)
    {
        var alpha = Db(
            "alpha",
            Review("7001"),
            Review("7002", artifactCommit),
            Snapshot(("7001", "Merged", Now.AddDays(-2)), ("7002", "Merged", Now.AddDays(-1)))
        );

        var (scan, plan) = await PlanAsync(new FakeHttpMessageHandler(), [], Target("alpha", alpha));

        plan.Select(entry => entry.Pick.Pr.Label).Should().Equal("Nova !7001");
        scan.Counts.Should().BeEquivalentTo(new Dictionary<string, int> { ["Start"] = 1, ["no-artifact-commit"] = 1 });
        JudgeBatch
            .Summary(scan, plan, [], [], Settings, execute: false, "ok", 0, Now)
            .Should()
            .Contain("- PRs: 1 to start, 1 review has no verified artifact commit.");
    }

    [Fact]
    public void Shard_parses_i_of_n_and_the_cli_refuses_anything_else()
    {
        JudgeBatch.Shard.Parse(null).Should().BeNull();
        JudgeBatch.Shard.Parse("0/1").Should().Be(new JudgeBatch.Shard(0, 1));
        JudgeBatch.Shard.Parse("4/5").Should().Be(new JudgeBatch.Shard(4, 5));
        CliOptions.Parse(["judge", "batch", "--targets", "t.tsv", "--shard", "4/5"]).Get("shard").Should().Be("4/5");
        foreach (var bad in new[] { "5/5", "0/0", "1/0", "-1/2", "+1/2", " 1/2", "1/2/3", "a/2", "1", "" })
        {
            FluentActions
                .Invoking(() => JudgeBatch.Shard.Parse(bad))
                .Should()
                .Throw<ArgumentException>(bad)
                .WithMessage($"--shard {bad}: expected i/n with 0 <= i < n*");
            FluentActions
                .Invoking(() => CliOptions.Parse(["judge", "batch", "--targets", "t.tsv", "--shard", bad]))
                .Should()
                .Throw<ArgumentException>(bad);
        }
    }

    [Fact]
    public void A_shard_takes_each_PR_number_once_across_its_lanes()
    {
        var shards = Enumerable.Range(0, 5).Select(i => new JudgeBatch.Shard(i, 5)).ToList();
        foreach (var pr in new[] { "0", "5764750", "5815488", "5833977", "5838315", "abc", "" })
            shards.Count(shard => shard.Takes(pr)).Should().Be(1, pr);
        new JudgeBatch.Shard(0, 5).Takes("abc").Should().BeTrue("a PR id that is not a number is shard 0's");
        new JudgeBatch.Shard(3, 5).Takes("5764753").Should().BeTrue();
    }

    [Fact]
    public async Task A_preview_scan_writes_nothing()
    {
        var capped = Receipt("capped", thread: "thread-capped");
        capped["frozenAt"] = Now.AddHours(-1);
        var alpha = Db(
            "alpha",
            Review("7001"),
            Review("7002"),
            Snapshot(("7001", "Merged", Now.AddDays(-2)), ("7002", "Merged", Now.AddDays(-1))),
            Judgment("7001", 1, capped),
            ($"reviewd/judge-skip/{RepoKey}/7003", $$"""{"reason":"not-completed","at":"{{Now:O}}"}""")
        );
        // Windows keeps a pooled connection's file open, and that open refuses a reader.
        SqliteConnection.ClearAllPools();
        var before = Directory.GetFiles(_dir).ToDictionary(path => path, File.ReadAllBytes);
        var missing = Path.Combine(_dir, "missing.db");
        var host = new FakeHttpMessageHandler()
            .OnJson(HttpMethod.Get, "thread-capped/run-state", """{"isInProgress":false,"currentRunId":null}""")
            .OnJson(HttpMethod.Get, "thread-capped/subagents?recursive=true", Tree("completed"))
            .OnJson(HttpMethod.Get, "thread-capped/usage", """{"preferredCostMicros":1000000,"completeness":2}""");

        var (scan, _) = await PlanAsync(host, [], Target("alpha", alpha), Target("gone", missing));

        scan.Skipped.Should().Equal(("gone", "its database file is missing or cannot be opened"));
        File.Exists(missing).Should().BeFalse("a read-only open never creates a store");
        Directory.GetFiles(_dir).Should().BeEquivalentTo(before.Keys, "no -wal, -shm or journal file appears");
        SqliteConnection.ClearAllPools();
        foreach (var (path, bytes) in before)
            File.ReadAllBytes(path).Should().Equal(bytes, path);
        host.Requests.Should().OnlyContain(request => request.Method == HttpMethod.Get);
    }

    // ---- fixtures

    private static string Tree(string status) =>
        $$"""{"schemaVersion":1,"nodes":[{"agentId":"agent-1","threadId":"subagent-x-agent-1","parentThreadId":"thread-capped","depth":1,"template":"code-reviewer:finding-verifier","status":"{{status}}"}]}""";

    private const string Active = """{"threadId":"t","isInProgress":true,"currentRunId":"run-1"}""";
    private const string Idle = """{"threadId":"t","isInProgress":false,"currentRunId":null}""";

    private Task<(JudgeBatch.Scan Scan, List<(JudgeBatch.Pick Pick, string? Wait)> Plan)> PlanAsync(
        FakeHttpMessageHandler host,
        List<(string Name, string Reason)> broken,
        params JudgeBatch.Target[] targets
    ) => PlanAsync(host, broken, Settings, targets);

    private static async Task<(JudgeBatch.Scan Scan, List<(JudgeBatch.Pick Pick, string? Wait)> Plan)> PlanAsync(
        FakeHttpMessageHandler host,
        List<(string Name, string Reason)> broken,
        JudgeBatch.Settings settings,
        params JudgeBatch.Target[] targets
    )
    {
        using var http = new HttpClient(host) { BaseAddress = new Uri("http://localhost/") };
        var client = new LmStreamingS2SClient(http, null, null, null);
        var scan = await JudgeBatch.ScanAsync([.. targets], broken, client, settings, Now, CancellationToken.None);
        return (scan, JudgeBatch.Plan(scan, [], latched: false, 0, int.MaxValue, settings, Now));
    }

    private async Task<string[]> SummaryLinesAsync(FakeHttpMessageHandler host, string db)
    {
        var (scan, plan) = await PlanAsync(host, [], Target("alpha", db));
        return JudgeBatch
            .Summary(scan, plan, [], [], Settings, execute: false, "ok", 0, Now)
            .ReplaceLineEndings("\n")
            .Split('\n');
    }

    // A failed judgment that read $0.40 so far under a $15 cap, with its sub-agents settled or not; it ran for one hour.
    private string FailedJudgmentDb(string name, bool settled, int startedHoursAgo = 2)
    {
        var failed = Receipt(
            "failed",
            thread: "thread-failed",
            usd: 0.40m,
            startedAt: Now.AddHours(-startedHoursAgo),
            finishedAt: Now.AddHours(1 - startedHoursAgo)
        );
        failed["caps"]!["maxUsd"] = 15m;
        failed["descendantsSettled"] = settled;
        return Db(name, Review("7001"), Snapshot(("7001", "Merged", Now.AddDays(-1))), Judgment("7001", 1, failed));
    }

    private static JudgeBatch.Target Target(string name, string db) => new(name, db, "session-1", Workspace);

    private string Db(string name, params (string Key, string Value)[] rows)
    {
        var path = Path.Combine(_dir, name + ".db");
        using var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();
        using var create = connection.CreateCommand();
        create.CommandText = "CREATE TABLE cache (key TEXT PRIMARY KEY, value TEXT NOT NULL)";
        create.ExecuteNonQuery();
        foreach (var (key, value) in rows)
        {
            using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO cache (key, value) VALUES (@key, @value)";
            insert.Parameters.AddWithValue("@key", key);
            insert.Parameters.AddWithValue("@value", value);
            insert.ExecuteNonQuery();
        }
        return path;
    }

    private static (string Key, string Value) Review(string pr, string? artifactCommit = ArtifactCommit) =>
        (
            $"reviewd/review-run/{RepoKey}/{pr}",
            new JsonObject
            {
                ["repository"] = Repo,
                ["prId"] = pr,
                ["stage"] = "completed-verified",
                ["workspaceId"] = Workspace,
                ["artifactCommit"] = artifactCommit is null ? null : new JsonObject { ["commit"] = artifactCommit },
            }.ToJsonString()
        );

    private const string ArtifactCommit = "0123456789abcdef0123456789abcdef01234567";

    private static (string Key, string Value) Snapshot(
        params (string Pr, string Lifecycle, DateTimeOffset Changed)[] prs
    )
    {
        var snapshot = new JsonObject();
        foreach (var (pr, lifecycle, changed) in prs)
            snapshot[pr] = new JsonObject { ["Lifecycle"] = lifecycle, ["LastChangedAt"] = changed };
        return ($"reviewd/pr-snapshot/{RepoKey}", snapshot.ToJsonString());
    }

    private static (string Key, string Value) Judgment(string pr, int n, JsonObject receipt)
    {
        receipt["n"] = n;
        return ($"reviewd/judge-run/{RepoKey}/{pr}/{n}", receipt.ToJsonString());
    }

    private static JsonObject Receipt(
        string stage,
        string? thread = null,
        decimal? usd = 1m,
        DateTimeOffset? startedAt = null,
        DateTimeOffset? finishedAt = null
    ) =>
        new()
        {
            ["stage"] = stage,
            ["judgeThreadId"] = thread,
            ["identity"] = new JsonObject
            {
                ["provider"] = "gpt-6.1-sol",
                ["effort"] = "xhigh",
                ["mode"] = "workspace-agent",
                ["promptTemplateSha256"] = Settings.PromptSha256,
            },
            ["caps"] = new JsonObject { ["maxUsd"] = 5m },
            ["judgeUsage"] = new JsonObject
            {
                ["measured"] = usd is not null,
                ["usd"] = usd,
                ["completeness"] = 2,
            },
            ["startedAt"] = startedAt ?? Now.AddHours(-1),
            ["finishedAt"] = finishedAt,
        };
}
