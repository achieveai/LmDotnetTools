extern alias Reviewd;

using System.Text.Json.Nodes;
using AchieveAi.LmDotnetTools.Misc.Storage;
using AchieveAi.LmDotnetTools.Sandbox;
using CodeReviewDaemon.Sample.Agents;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using Microsoft.Extensions.Configuration;
using Reviewd::Reviewd.Sample;

namespace CodeReviewDaemon.Sample.Tests.Scenarios;

/// <summary>
///     <c>review run</c> without <c>--slot</c>: the first free or lapsed-lease slot is used, held slots are skipped with
///     nothing claimed, and no usable slot exits 3. The fake sandbox stops every run right after admission.
/// </summary>
public sealed class ReviewdAutoSlotTests : IDisposable
{
    private const string Stop = "stop after admission";
    private static readonly string Head = new('a', 40);

    private readonly string _database = Path.Combine(Path.GetTempPath(), $"reviewd-autoslot-{Guid.NewGuid():N}.db");
    private readonly List<string> _phases = [];
    private readonly HashSet<int> _notIdle = [];
    private readonly FakeHttpMessageHandler _handler;

    /// <summary>Runs once on the first host-contract request, after the owner check and before the claim.</summary>
    private Action? _beforeClaim;

    /// <summary>What the host reports for every conversation's run state.</summary>
    private bool _inProgress;

    /// <summary>The gateway refuses every operation submit at its record limit.</summary>
    private bool _refuseAtCapacity;

    /// <summary>The configured <c>Reviewd:LanesPerRepo</c>, when set.</summary>
    private int? _lanes;

    public ReviewdAutoSlotTests()
    {
        _handler = new FakeHttpMessageHandler()
            .On(
                request => request.RequestUri!.ToString().Contains("/capabilities", StringComparison.Ordinal),
                _ =>
                {
                    Interlocked.Exchange(ref _beforeClaim, null)?.Invoke();
                    return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                    {
                        Content = new StringContent(
                            """{"messageIdempotency":true,"spawnSuppression":true,"rootReasoningEffort":true,"conversationWorkingDirectory":true}""",
                            System.Text.Encoding.UTF8,
                            "application/json"
                        ),
                    };
                }
            )
            .On(
                request => request.RequestUri!.ToString().Contains("/run-state", StringComparison.Ordinal),
                _ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        _inProgress
                            ? """{"isInProgress":true,"currentRunId":"run-1"}"""
                            : """{"isInProgress":false,"currentRunId":null}""",
                        System.Text.Encoding.UTF8,
                        "application/json"
                    ),
                }
            )
            .OnJson(HttpMethod.Post, "/freeze", """{"frozen":true}""")
            .OnJson(HttpMethod.Post, "/unfreeze", """{"frozen":false}""")
            .OnJson(HttpMethod.Get, "/api/conversations?", "[]")
            .OnJson(HttpMethod.Get, "/subagents?", """{"schemaVersion":1,"nodes":[]}""");
        using var connection = PrThreadBindings.Connection(_database);
        new SqliteKvStore(connection)
            .SetAsync(
                $"reviewd/pr-snapshot/{Repo.NormalizedKey}",
                new JsonObject
                {
                    ["17"] = new JsonObject
                    {
                        ["Lifecycle"] = "Open",
                        ["IsDraft"] = false,
                        ["HeadSha"] = Head,
                    },
                }
            )
            .GetAwaiter()
            .GetResult();
    }

    private static readonly RepoIdentity Repo = PrCommands.ParseRepo("org/project/Nova");

    [Fact]
    public async Task Skips_held_leased_and_natively_busy_slots_and_claims_the_first_free_one()
    {
        await HoldAsync(9, 0);
        _notIdle.Add(2);
        var before = await ReadAllAsync();
        using (ReviewRunner.AcquireLease(_database, ReviewRunner.SlotLeaseIdentity("workspace", Repo, 1)))
        {
            var run = () => RunAsync();
            await run.Should().ThrowAsync<InvalidOperationException>().WithMessage(Stop);
        }

        _phases.Should().Equal("prepare 17/2", "prepare 17/3");
        var attempt = (await GetAsync(AttemptKey(17)))!;
        attempt["slot"]!.GetValue<int>().Should().Be(3);
        attempt["slotSelection"]!["skipped"]!
            .AsArray()
            .Select(skip => skip!["slot"]!.GetValue<int>())
            .Should()
            .Equal(0, 1, 2);
        // The busy native slot was claimed, refused by prepare, and released without any sandbox cleanup.
        (await GetAsync(OwnerKey(2)))!["state"]!
            .GetValue<string>()
            .Should()
            .Be("released");
        (await GetAsync(OwnerKey(3)))!["state"]!.GetValue<string>().Should().Be("owned");
        // The held slot's rows are untouched.
        var after = await ReadAllAsync();
        after[AttemptKey(9)].Should().Be(before[AttemptKey(9)]);
        after[OwnerKey(0)].Should().Be(before[OwnerKey(0)]);
    }

    [Theory]
    [InlineData("queued", 1)]
    [InlineData("verified", 0)]
    public async Task A_live_batch_entry_keeps_its_slot_but_a_finished_one_does_not(string status, int expected)
    {
        await PutAsync(
            "reviewd/batch-state/X",
            JsonNode.Parse(
                $$"""{"schemaVersion":1,"scope":{"workspace":"workspace"},"entries":[{"repo":"org/project/Nova","pr":"40","slot":0,"status":"{{status}}"}]}"""
            )!
        );

        var run = () => RunAsync();

        await run.Should().ThrowAsync<InvalidOperationException>().WithMessage(Stop);
        _phases.Should().Equal($"prepare 17/{expected}");
    }

    [Fact]
    public async Task A_prepare_the_gateway_refused_at_capacity_releases_only_its_own_claim()
    {
        _refuseAtCapacity = true;

        var run = () => RunAsync("--slots", "0");

        await run.Should().ThrowAsync<SandboxException>();
        _phases.Should().Equal("prepare 17/0");
        (await GetAsync(AttemptKey(17)))!["stage"]!.GetValue<string>().Should().Be("failed-released");
        (await GetAsync(OwnerKey(0)))!["state"]!.GetValue<string>().Should().Be("released");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_verified_receipt_frees_its_slot_once_the_slot_owner_moved_past_its_claim(bool released)
    {
        // PR 9's verified review in slot 0 is followed by a re-review in slot 1, so its latest attempt is in flight.
        var at = DateTimeOffset.UtcNow.ToString("O");
        var receipt = Attempt(9, 0, "completed-verified", at);
        await PutAsync($"reviewd/review-run/{Repo.NormalizedKey}/9", receipt);
        await HoldAsync(9, 1);
        var slotOwner = Claim(receipt, DateTimeOffset.UtcNow.AddMinutes(30));
        if (released)
            slotOwner["state"] = "released";
        await PutAsync(OwnerKey(0), slotOwner);

        var run = () => RunAsync("--slots", "0");

        if (released)
        {
            await run.Should().ThrowAsync<InvalidOperationException>().WithMessage(Stop);
            _phases.Should().Equal("prepare 17/0");
        }
        else
        {
            (await run()).Should().Be(ReviewRunner.NoSlotExitCode);
            _phases.Should().BeEmpty();
        }
    }

    [Fact]
    public async Task No_usable_slot_exits_3_without_claims_writes_or_remote_calls()
    {
        for (var slot = 0; slot < 6; slot++)
            await HoldAsync(100 + slot, slot);
        var before = await ReadAllAsync();

        (await RunAsync()).Should().Be(ReviewRunner.NoSlotExitCode);

        (await ReadAllAsync()).Should().Equal(before);
        _phases.Should().BeEmpty();
        _handler.Requests.Should().BeEmpty();
    }

    [Theory]
    [InlineData(-1, false, "reclaimed")]
    [InlineData(30, false, "held")]
    [InlineData(-1, true, "in progress")]
    [InlineData(null, false, "held")]
    public async Task An_owner_whose_lease_lapsed_is_reclaimed_like_the_reaper_and_its_slot_used(
        int? leaseMinutes,
        bool inProgress,
        string outcome
    )
    {
        await VerifiedOwnerAsync(9, 0, leaseMinutes is { } minutes ? DateTimeOffset.UtcNow.AddMinutes(minutes) : null);
        _inProgress = inProgress;

        var run = () => RunAsync("--slots", "0");

        if (outcome == "reclaimed")
        {
            await run.Should().ThrowAsync<InvalidOperationException>().WithMessage(Stop);
            _phases.Should().Equal("cleanup 9/0", "prepare 17/0");
            (await GetAsync(AttemptKey(9)))!["cleanupState"]!.GetValue<string>().Should().Be("released");
            (await GetAsync(AttemptKey(17)))!["slotSelection"]!["reclaimed"]!.GetValue<string>().Should().Be("9");
            return;
        }
        (await run()).Should().Be(ReviewRunner.NoSlotExitCode);
        _phases.Should().BeEmpty();
        (await GetAsync(AttemptKey(9)))!["cleanupState"]!.GetValue<string>().Should().Be("owned");
        // A fresh or undated lease is never touched on the host; a live review is never frozen.
        _handler.CountRequests("/freeze").Should().Be(0);
        if (outcome == "held")
            _handler.CountRequests("/run-state").Should().Be(0);
    }

    [Fact]
    public async Task A_slot_claimed_by_another_run_after_the_check_is_skipped_for_the_next_one()
    {
        _beforeClaim = () => HoldAsync(50, 0).GetAwaiter().GetResult();

        var run = () => RunAsync();

        await run.Should().ThrowAsync<InvalidOperationException>().WithMessage(Stop);
        _phases.Should().Equal("prepare 17/1");
        var skipped = (await GetAsync(AttemptKey(17)))!["slotSelection"]!["skipped"]!.AsArray();
        skipped.Select(skip => skip!["slot"]!.GetValue<int>()).Should().Equal(0);
        skipped[0]!["reason"]!.GetValue<string>().Should().Contain("durable owner");
        (await GetAsync(OwnerKey(0)))!["prId"]!.GetValue<string>().Should().Be("50");
    }

    [Fact]
    public async Task A_PR_that_owns_a_slot_is_never_moved()
    {
        await VerifiedOwnerAsync(17, 4, DateTimeOffset.UtcNow.AddMinutes(30));

        (await RunAsync("--cleanup-only")).Should().Be(0);

        _phases.Should().Equal("cleanup 17/4");
    }

    [Theory]
    [InlineData("--cleanup-only")]
    [InlineData("--resume")]
    public async Task Recovery_without_a_recorded_slot_is_refused_rather_than_placed_in_a_new_slot(string recovery)
    {
        var run = () => RunAsync(recovery);

        await run.Should().ThrowAsync<InvalidOperationException>().WithMessage("*records this PR's slot*");
        _phases.Should().BeEmpty();
    }

    [Fact]
    public async Task An_explicit_slot_releases_its_own_claim_when_prepare_finds_the_slot_busy()
    {
        _notIdle.Add(2);

        var run = () => RunAsync("--slot", "2");

        await run.Should().ThrowAsync<ReviewRunner.SlotUnavailableException>().WithMessage("*not idle*");
        (await GetAsync(OwnerKey(2)))!["state"]!.GetValue<string>().Should().Be("released");
        var attempt = (await GetAsync(AttemptKey(17)))!;
        attempt["stage"]!.GetValue<string>().Should().Be("failed-released");
        attempt["cleanupState"]!.GetValue<string>().Should().Be("released");
        _phases.Should().Equal("prepare 17/2");
    }

    [Theory]
    [InlineData("prepare", 3, """{"status":"slot-not-idle","reason":"busy"}""", "not-idle")]
    [InlineData("prepare", 3, "Traceback", "settled")]
    [InlineData("prepare", 3, """{"status":"failed"}""", "settled")]
    [InlineData("prepare", 1, """{"status":"slot-not-idle"}""", "settled")]
    [InlineData("cleanup", 3, """{"status":"slot-not-idle"}""", "settled")]
    [InlineData("prepare", 0, """{"mergeSha":"m"}""", "ok")]
    public void Only_prepare_exit_3_with_its_status_line_is_a_busy_slot(
        string phase,
        int exit,
        string output,
        string expected
    )
    {
        var settle = () => ReviewRunner.Settle(phase, exit, output, null, "op");

        switch (expected)
        {
            case "not-idle":
                settle.Should().Throw<ReviewRunner.SlotNotIdleException>().WithMessage("busy");
                break;
            case "settled":
                settle
                    .Should()
                    .Throw<ReviewRunner.SettledSandboxException>()
                    .Which.Should()
                    .NotBeOfType<ReviewRunner.SlotNotIdleException>();
                break;
            default:
                settle().Should().ContainKey("mergeSha");
                break;
        }
    }

    [Theory]
    [InlineData("--slot", "1", "--slots", "2")]
    [InlineData("--slots", "1,1")]
    [InlineData("--slots", "6")]
    [InlineData("--slots", "a")]
    [InlineData("--slot", "6")]
    public void Review_run_rejects_invalid_slot_options(params string[] extra)
    {
        var parse = () => CliOptions.Parse([.. Arguments(), .. extra]);

        parse.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Review_run_accepts_a_missing_slot_and_defaults_to_every_slot()
    {
        CliOptions.ReviewSlots(CliOptions.Parse(Arguments())).Should().Equal(0, 1, 2, 3, 4, 5);
        CliOptions.ReviewSlots(CliOptions.Parse([.. Arguments(), "--slots", "4, 2"])).Should().Equal(4, 2);
    }

    [Fact]
    public async Task Without_slots_only_the_configured_lanes_are_tried()
    {
        for (var slot = 0; slot < 3; slot++)
            await HoldAsync(100 + slot, slot);
        _lanes = 3;

        (await RunAsync()).Should().Be(ReviewRunner.NoSlotExitCode);

        // Slot 3 is not one of the runner's lanes, so it is never prepared.
        _phases.Should().BeEmpty();
    }

    [Theory]
    [InlineData("0")]
    [InlineData("7")]
    [InlineData("three")]
    public void An_out_of_range_lane_count_is_refused(string lanes)
    {
        using var config = Config(lanes);

        var read = () => config.LanesPerRepo;

        read.Should().Throw<ArgumentException>().WithMessage("*LanesPerRepo*");
    }

    [Fact]
    public async Task Review_slots_reports_each_slot_from_the_database_without_writing()
    {
        var now = DateTimeOffset.UtcNow;
        await HoldAsync(9, 0);
        await VerifiedOwnerAsync(10, 1, now.AddMinutes(-1));
        await PutAsync(
            "reviewd/batch-state/X",
            JsonNode.Parse(
                """{"schemaVersion":1,"scope":{"workspace":"workspace"},"entries":[{"repo":"org/project/Nova","pr":"40","slot":2,"status":"queued"}]}"""
            )!
        );
        var released = Claim(Attempt(11, 3, "completed-verified", now.ToString("O")), now.AddMinutes(-1));
        released["state"] = "released";
        await PutAsync(OwnerKey(3), released);
        await PutAsync(OwnerKey(4), Claim(Attempt(12, 4, "review-running", now.ToString("O")), null));
        var before = await ReadAllAsync();

        var rows = await SlotStatus.ReadAsync(_database, "workspace", [Repo], [0, 1, 2, 3, 4, 5], now, default);

        rows.Select(row => $"{row["slot"]}:{row["state"]}:{row["prId"]}")
            .Should()
            .Equal("0:owned:9", "1:expired:10", "2:queued:", "3:free:", "4:owned:12", "5:free:");
        (await ReadAllAsync()).Should().Equal(before);
    }

    [Theory]
    [InlineData("--repo", "org/project/Nova", "--db", "x.db")]
    [InlineData("--repo", "org/project/Nova", "--repos", "org/project/Nova", "--db", "x.db", "--workspace", "w")]
    [InlineData("--repo", "org/project/Nova", "--db", "x.db", "--workspace", "w", "--slots", "6")]
    public void Review_slots_rejects_incomplete_options(params string[] extra)
    {
        var parse = () => CliOptions.Parse(["review", "slots", .. extra]);

        parse.Should().Throw<ArgumentException>();
    }

    [SkippableTheory]
    [InlineData(
        "stale",
        3,
        """{"status": "slot-not-idle", "reason": "Native slot is not at the idle baseline: off baseline"}"""
    )]
    [InlineData("idle", 0, "admitted")]
    [InlineData("broken", 1, "")]
    public async Task Prepare_refuses_a_slot_off_the_idle_baseline_as_not_idle(string verify, int exit, string output)
    {
        // Only a ReviewError from the baseline check is a busy slot; any other failure still surfaces.
        const string harness = """
            import ast, json, sys
            tree = ast.parse(open(sys.argv[1], encoding='utf-8').read())
            rules = [n for n in tree.body if isinstance(n, ast.FunctionDef) and n.name in ('slot_not_idle', 'admit_baseline')]
            class ReviewError(Exception): pass
            scope = {'json': json, 'sys': sys, 'ReviewError': ReviewError}
            exec(compile(ast.Module(rules, []), 'admission', 'exec'), scope)
            def stale(): raise ReviewError('off baseline')
            def broken(): raise RuntimeError('git is missing')
            scope['admit_baseline']({'stale': stale, 'idle': lambda: None, 'broken': broken}[sys.argv[2]])
            print('admitted')
            """;
        var (exitCode, stdout, stderr) = await RunSandboxRuleAsync(harness, verify);

        exitCode.Should().Be(exit, stderr);
        stdout.Trim().Should().Be(output);
    }

    [SkippableTheory]
    // A reclaim that released the native marker and then died before releasing the SQLite owner must be able to
    // finish on the next reap, or the slot stays blocked forever (seen live: 8 hours after a batch restart).
    [InlineData("released", "mine", "discard", true)]
    [InlineData("released", "other", "discard", true)]
    [InlineData("released", "mine", "failedPreparation", true)]
    [InlineData("owned", "other", "failedPreparation", true)]
    [InlineData("missing", "mine", "failedPreparation", true)]
    // A live owner, or a normal cleanup, never takes over someone else's marker or re-releases its own.
    [InlineData("owned", "other", "discard", false)]
    [InlineData("owned", "mine", "discard", false)]
    [InlineData("released", "mine", "none", false)]
    [InlineData("missing", "mine", "discard", false)]
    public async Task Cleanup_adopts_only_a_natively_free_or_orphaned_marker(
        string state,
        string token,
        string mode,
        bool adopted
    )
    {
        const string harness = """
            import ast, json, sys
            tree = ast.parse(open(sys.argv[1], encoding='utf-8').read())
            rules = [n for n in tree.body if isinstance(n, ast.FunctionDef) and n.name == 'adoptable']
            scope = {}
            exec(compile(ast.Module(rules, []), 'cleanup', 'exec'), scope)
            state, token, mode = sys.argv[2:5]
            previous = None if state == 'missing' else {'claimToken': token, 'pr': 7, 'state': state}
            args = {'claimToken': 'mine'}
            if mode != 'none':
                args[mode] = True
            print(scope['adoptable'](previous, args))
            """;
        var (exitCode, stdout, stderr) = await RunSandboxRuleAsync(harness, state, token, mode);

        exitCode.Should().Be(0, stderr);
        stdout.Trim().Should().Be(adopted ? "True" : "False");
    }

    [SkippableTheory]
    // A forced discard resets the slot whatever the native records say, and names each disagreement for the log.
    [InlineData("missing", "mine", 7, "", """["owner marker missing"]""")]
    [InlineData("owned", "other", 7, "", """["owner marker held by claim other for PR 7"]""")]
    [InlineData("released", "mine", 7, "", """["owner marker already released"]""")]
    [InlineData("owned", "mine", 7, "8", """["slot prepared for PR 8"]""")]
    [InlineData("owned", "mine", 7, "7", "[]")]
    [InlineData("owned", "mine", 7, "", "[]")]
    public async Task A_forced_cleanup_names_every_bookkeeping_disagreement_it_overrides(
        string state,
        string token,
        int markerPr,
        string preparedPr,
        string expected
    )
    {
        const string harness = """
            import ast, json, sys
            tree = ast.parse(open(sys.argv[1], encoding='utf-8').read())
            rules = [n for n in tree.body if isinstance(n, ast.FunctionDef) and n.name == 'forced_overrides']
            scope = {}
            exec(compile(ast.Module(rules, []), 'cleanup', 'exec'), scope)
            state, token, marker_pr, prepared_pr = sys.argv[2:6]
            previous = None if state == 'missing' else {'claimToken': token, 'pr': int(marker_pr), 'state': state}
            prepared = int(prepared_pr) if prepared_pr else None
            print(json.dumps(scope['forced_overrides'](previous, {'claimToken': 'mine'}, 7, prepared)))
            """;
        var (exitCode, stdout, stderr) = await RunSandboxRuleAsync(
            harness,
            state,
            token,
            markerPr.ToString(System.Globalization.CultureInfo.InvariantCulture),
            preparedPr
        );

        exitCode.Should().Be(0, stderr);
        stdout.Trim().Should().Be(expected);
    }

    [SkippableFact]
    public async Task A_forced_cleanup_delayed_past_another_reset_cannot_discard_the_next_owner()
    {
        // Two reapers read the same lapsed claim A. One resets the slot, then a lane admits PR 8 under claim B. The
        // other was delayed before the native lock and now runs its forced cleanup for A. Without a fence it would
        // overwrite B's marker and discard B's prepared slot; SQLite refuses A's release only afterward.
        var python = await ReviewdSummaryReplyTests.FindPythonAsync();
        Skip.If(python is null, "no Python interpreter on PATH, so the sandbox script cannot be run");
        Skip.IfNot(OperatingSystem.IsLinux(), "the sandbox script scans /proc, so it runs only on Linux");
        var root = Directory.CreateTempSubdirectory("reviewd-sandbox-").FullName;
        try
        {
            var scripts = Directory.CreateDirectory(Path.Combine(root, ".claude/skills/review-setup/scripts"));
            foreach (var (name, source) in NativeStubs)
                await File.WriteAllTextAsync(Path.Combine(scripts.FullName, name), source);
            var script = Path.Combine(root, "ReviewRunnerSandbox.py");
            using (
                var resource = typeof(ReviewRunner).Assembly.GetManifestResourceStream(
                    "Reviewd.Sample.ReviewRunnerSandbox.py"
                )!
            )
            using (var file = File.Create(script))
                await resource.CopyToAsync(file);
            var state = Directory.CreateDirectory(Path.Combine(root, "state")).FullName;
            var marker = Path.Combine(
                Directory.CreateDirectory(Path.Combine(state, "reviewd-owners")).FullName,
                "nova-2.json"
            );
            var prepared = Path.Combine(state, "prepared-nova-2.json");
            var discards = Path.Combine(state, "discards.log");
            await File.WriteAllTextAsync(marker, """{"claimToken": "A", "pr": 7, "state": "owned"}""");
            await File.WriteAllTextAsync(prepared, """{"pr": 7}""");

            (await PhaseAsync("cleanup", 7, "A", force: true)).ExitCode.Should().Be(0, "the first reaper resets A");
            // The stub setup stops right after admission wrote B's marker; B's prepared slot is then written by hand.
            (await PhaseAsync("prepare", 8, "B"))
                .Stderr.Should()
                .Contain("Setup stopped");
            await File.WriteAllTextAsync(prepared, """{"pr": 8}""");
            var stale = await PhaseAsync("cleanup", 7, "A", force: true);

            stale.ExitCode.Should().NotBe(0);
            stale.Stderr.Should().Contain("admitted again since this claim");
            JsonNode.Parse(await File.ReadAllTextAsync(marker))!["claimToken"]!.GetValue<string>().Should().Be("B");
            JsonNode.Parse(await File.ReadAllTextAsync(prepared))!["pr"]!.GetValue<int>().Should().Be(8);
            (await File.ReadAllLinesAsync(discards)).Should().Equal("7");
            // B itself still cleans up its own slot.
            (await PhaseAsync("cleanup", 8, "B"))
                .ExitCode.Should()
                .Be(0);
            (await File.ReadAllLinesAsync(discards)).Should().Equal("7", "8");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }

        async Task<(int ExitCode, string Stderr)> PhaseAsync(string phase, int pr, string claim, bool force = false)
        {
            var args = new JsonObject
            {
                ["org"] = "org",
                ["repo"] = "Nova",
                ["pr"] = pr,
                ["slot"] = 2,
                ["head"] = new string('h', 40),
                ["claimToken"] = claim,
            };
            if (phase == "cleanup")
            {
                args["discard"] = true;
                args["force"] = force;
            }
            var start = new System.Diagnostics.ProcessStartInfo(python!)
            {
                WorkingDirectory = root,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var argument in new[] { Path.Combine(root, "ReviewRunnerSandbox.py"), phase, args.ToJsonString() })
                start.ArgumentList.Add(argument);
            using var process = System.Diagnostics.Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = await process.StandardError.ReadToEndAsync();
            await stdout;
            await process.WaitForExitAsync();
            return (process.ExitCode, stderr);
        }
    }

    // Just enough of the installed review-setup helpers for the sandbox script's prepare and cleanup phases to run
    // against plain files: locks are no-ops, a discard logs the discarded PR, and setup always stops.
    private static readonly (string Name, string Source)[] NativeStubs =
    [
        (
            "review_common.py",
            """
            import contextlib, json
            from pathlib import Path
            from types import SimpleNamespace


            class ReviewError(Exception):
                pass


            class ReviewState:
                def __init__(self, root, read_only=False):
                    self.directory = Path(root) / 'state'
                    self.identity = 'test'

                @contextlib.contextmanager
                def lock(self, shared=False, phase=None):
                    yield

                @contextlib.contextmanager
                def slot_lock(self, repo_key, slot, phase=None):
                    yield

                def prepared_slot_path(self, repo_key, slot):
                    return self.directory / f'prepared-{repo_key}-{slot}.json'

                def load_pool_run(self, repo_key, slot, dry_run=False):
                    return None


            class SourcePool:
                def __init__(self, state, module):
                    self.repo_key = 'nova'

                def slot_relative_path(self, slot):
                    return f'slots/{slot}'

                def _verified_baseline(self):
                    return 'baseline'

                def _ensure_store(self):
                    pass


            def repo_slug(repo):
                return repo.lower()


            def safe_path(base, relative):
                return Path(base) / relative


            def run_git_read(path, args, check=True):
                return SimpleNamespace(stdout='head\n', returncode=0)


            def atomic_json(path, value):
                Path(path).write_text(json.dumps(value))


            def lock_session(timeout):
                return contextlib.nullcontext()


            def slot_writer_scope(*args, **kwargs):
                return contextlib.nullcontext()


            def git_status(*args, **kwargs):
                return ''


            def git_ignored_paths(*args, **kwargs):
                return []


            inspect_checkout_preservation = preserve_checkout_generation = verify_preservation_archive = None
            preservation_generation = file_hash = None
            """
        ),
        (
            "review_pool_admin.py",
            """
            def select_module_by_name(root, repo):
                return {'path': 'src/Nova', 'provider': 'github', 'owner': 'org', 'repository': 'Nova'}
            """
        ),
        (
            "review_github.py",
            """
            class GitHubClient:
                def __init__(self, owner, repository):
                    pass


            def fetch_pr(client, pr):
                return {'base': {'repo': {'name': 'Nova', 'owner': {'login': 'org'}}}}


            def live_state(payload):
                return {'prId': '8', 'status': 'active', 'isDraft': False, 'createdAt': None, 'headSha': 'h' * 40,
                        'mergeSha': 'm' * 40}


            def select_provider(*args, **kwargs):
                return None
            """
        ),
        (
            "review_git_prepare.py",
            """
            def _verify_postconditions(*args, **kwargs):
                pass


            def prepare(**kwargs):
                return 1
            """
        ),
        (
            "review_reset.py",
            """
            import json


            def _discard_prepared_slot(state, pool, slot, confirm=None):
                path = state.prepared_slot_path(pool.repo_key, slot)
                with open(state.directory / 'discards.log', 'a') as log:
                    log.write(f"{json.loads(path.read_text())['pr']}\n")
                path.unlink()
                return {'status': 'discarded'}


            def reset_pool_run_locked(*args, **kwargs):
                return {'status': 'reset'}
            """
        ),
        (
            "review_setup.py",
            """
            prepare = None


            def route(**kwargs):
                return 1
            """
        ),
    ];

    // Runs a harness that loads rules from the embedded sandbox script (passed as its first argument).
    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunSandboxRuleAsync(
        string harness,
        params string[] arguments
    )
    {
        var python = await ReviewdSummaryReplyTests.FindPythonAsync();
        Skip.If(python is null, "no Python interpreter on PATH, so the sandbox's rules cannot be run");
        var script = Path.Combine(Path.GetTempPath(), $"reviewd-sandbox-{Guid.NewGuid():N}.py");
        using (
            var resource = typeof(ReviewRunner).Assembly.GetManifestResourceStream(
                "Reviewd.Sample.ReviewRunnerSandbox.py"
            )!
        )
        using (var file = File.Create(script))
            await resource.CopyToAsync(file);
        try
        {
            var start = new System.Diagnostics.ProcessStartInfo(python!)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var argument in new[] { "-c", harness, script }.Concat(arguments))
                start.ArgumentList.Add(argument);
            using var process = System.Diagnostics.Process.Start(start)!;
            var stdout = await process.StandardOutput.ReadToEndAsync();
            var stderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            return (process.ExitCode, stdout, stderr);
        }
        finally
        {
            File.Delete(script);
        }
    }

    private static ReviewdConfig Config(string? lanes) =>
        new(
            new ConfigurationBuilder()
                .AddInMemoryCollection(
                    lanes is null ? [] : new Dictionary<string, string?> { ["Reviewd:LanesPerRepo"] = lanes }
                )
                .Build()
        );

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var file in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(_database) + "*"))
            File.Delete(file);
    }

    private string[] Arguments() =>
        [
            "review",
            "run",
            "--repo",
            "org/project/Nova",
            "--pr",
            "17",
            "--db",
            _database,
            "--session",
            "session",
            "--workspace",
            "workspace",
            "--provider",
            "test",
        ];

    private async Task<int> RunAsync(params string[] extra)
    {
        using var http = new HttpClient(_handler) { BaseAddress = new Uri("http://localhost/") };
        using var config = Config(_lanes?.ToString());
        return await ReviewRunner.RunAsync(
            CliOptions.Parse([.. Arguments(), "--execute", .. extra]),
            config,
            new LmStreamingS2SClient(http, null, null, null),
            Sandbox,
            CancellationToken.None,
            _ => Task.CompletedTask
        );
    }

    private Task<JsonObject> Sandbox(string phase, JsonObject args, string operation, CancellationToken ct)
    {
        var slot = args["slot"]!.GetValue<int>();
        _phases.Add($"{phase} {args["pr"]}/{slot}");
        if (phase == "cleanup")
            return Task.FromResult(
                new JsonObject
                {
                    ["claimToken"] = args["claimToken"]!.DeepClone(),
                    ["cleanBaselineVerified"] = true,
                    ["evidencePreserved"] = true,
                }
            );
        if (_notIdle.Contains(slot))
            throw new ReviewRunner.SlotNotIdleException("Native slot retains an owner.");
        if (_refuseAtCapacity)
            throw new SandboxException(SandboxErrorKind.Unavailable, "refused", 503)
            {
                ErrorCode = "operation_capacity_exhausted",
            };
        throw new InvalidOperationException(Stop);
    }

    /// <summary>A live review owns the slot: in flight, its lease fresh.</summary>
    private async Task HoldAsync(int pr, int slot)
    {
        var attempt = Attempt(pr, slot, "review-running", DateTimeOffset.UtcNow.ToString("O"));
        attempt["reviewInputId"] = "input";
        await PutAsync(AttemptKey(pr), attempt);
        await PutAsync(OwnerKey(slot), Claim(attempt, DateTimeOffset.UtcNow.AddMinutes(30)));
    }

    /// <summary>A verified review whose slot was never cleaned up, leased until <paramref name="lease"/> (null: undated).</summary>
    private async Task VerifiedOwnerAsync(int pr, int slot, DateTimeOffset? lease)
    {
        var at = DateTimeOffset.UtcNow.ToString("O");
        var attempt = Attempt(pr, slot, "completed-verified", at);
        attempt["finishedAt"] = at;
        attempt["threadId"] = $"t{pr}";
        attempt["artifactCommit"] = new JsonObject { ["commit"] = new string('c', 40) };
        await PutAsync(AttemptKey(pr), attempt);
        await PutAsync($"reviewd/review-run/{Repo.NormalizedKey}/{pr}", attempt.DeepClone());
        await PutAsync(OwnerKey(slot), Claim(attempt, lease));
        await PrThreadBindings.SetAsync(_database, Repo, pr.ToString(), $"t{pr}", CancellationToken.None);
    }

    private static JsonObject Attempt(int pr, int slot, string stage, string at) =>
        new()
        {
            ["repository"] = "org/project/Nova",
            ["prId"] = pr.ToString(),
            ["slot"] = slot,
            ["workspaceId"] = "workspace",
            ["sessionId"] = "session",
            ["providerId"] = "test",
            ["headSha"] = Head,
            ["stage"] = stage,
            ["cleanupState"] = "owned",
            ["claimToken"] = $"token-{pr}",
            ["startedAt"] = at,
        };

    private static JsonObject Claim(JsonObject attempt, DateTimeOffset? lease)
    {
        var claim = attempt.DeepClone().AsObject();
        claim["state"] = "owned";
        if (lease is { } expiry)
            claim["leaseExpiresAt"] = expiry;
        return claim;
    }

    private static string AttemptKey(int pr) => $"reviewd/review-attempt/{Repo.NormalizedKey}/{pr}";

    private static string OwnerKey(int slot) => SlotRelease.OwnerKey("workspace", Repo.NormalizedKey, slot);

    private async Task PutAsync(string key, JsonNode value)
    {
        using var connection = PrThreadBindings.Connection(_database);
        await new SqliteKvStore(connection).SetAsync(key, value);
    }

    private async Task<JsonObject?> GetAsync(string key)
    {
        using var connection = PrThreadBindings.Connection(_database);
        return await new SqliteKvStore(connection).GetAsync<JsonObject>(key);
    }

    private async Task<Dictionary<string, string>> ReadAllAsync()
    {
        using var connection = PrThreadBindings.Connection(_database);
        await connection.OpenAsync();
        using var query = connection.CreateCommand();
        query.CommandText = "SELECT key,value FROM cache";
        var rows = new Dictionary<string, string>();
        using var reader = await query.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            rows[reader.GetString(0)] = reader.GetString(1);
        return rows;
    }
}
