extern alias Reviewd;

using System.Text.Json.Nodes;
using AchieveAi.LmDotnetTools.Misc.Storage;
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
        using var config = new ReviewdConfig(new ConfigurationBuilder().Build());
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
