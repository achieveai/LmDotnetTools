extern alias Reviewd;

using System.Text.Json.Nodes;
using CodeReviewDaemon.Sample.Agents;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using Reviewd::Reviewd.Sample;

namespace CodeReviewDaemon.Sample.Tests.Scenarios;

/// <summary>
///     <c>review finish-commit</c>: a run killed after its artifact commit landed, before it wrote its receipt, is
///     finished by its own resume, never by a receipt the command writes. Each run is a real <c>ReviewRunner.RunAsync</c>
///     on <see cref="ScriptedReviewRun"/>.
/// </summary>
// The command prints its result to stdout, and the runs log to stderr; both are process-wide.
[Collection(ConsoleCapturingCollection.Name)]
public sealed class ReviewdFinishCommitTests
{
    // The run stops right after it saved commit-completed, still owning its slot: the batch holds that review, and a
    // resume refuses its stage. The command sends it back to verify-pending under the same claim and prints the resume
    // to run, since the batch keeps its entry held; that resume re-verifies, commits again and writes the receipt, so
    // the batch reconciles it as verified and the slot is freed. The resume run is the printed command itself, as
    // PowerShell passes it: it keeps the run's mode (the conversation is bound to it) and reasoning effort.
    [Theory]
    [InlineData(null, null)]
    [InlineData("security-review", "high")]
    public async Task A_run_killed_after_its_commit_landed_is_finished_and_reconciles_as_verified(
        string? mode,
        string? effort
    )
    {
        await using var run = await ScriptedReviewRun.CreateAsync();
        run.Mode = mode;
        run.ReasoningEffort = effort;
        var killed = await KillAfterCommitAsync(run);
        killed["stage"]!.GetValue<string>().Should().Be("commit-completed");
        killed["cleanupState"]!.GetValue<string>().Should().Be("owned");
        (await run.Store.GetAsync<JsonObject>(run.RunKey)).Should().BeNull("the run never wrote its receipt");
        ReviewBatch.Resumable(killed).Should().BeFalse("the batch holds a commit-completed owner");
        run.HostListsThread = true;
        await FluentActions
            .Invoking(() => run.RunAsync(resume: true, CancellationToken.None))
            .Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("Only identified admitted review or confirmation stages can resume.");

        var (exit, printed) = await FinishCommitAsync(run, execute: true);

        exit.Should().Be(0);
        var finishing = await run.ReadAsync(run.AttemptKey);
        finishing["stage"]!.GetValue<string>().Should().Be("verify-pending");
        finishing["claimToken"]!.GetValue<string>().Should().Be(killed["claimToken"]!.GetValue<string>());
        (await run.Store.GetAsync<JsonObject>(run.RunKey)).Should().BeNull("the command writes no receipt");
        var next = printed!["next"]!.GetValue<string>();
        next.Should()
            .Be(
                $"reviewd review run --repo '{ScriptedReviewRun.Repo.DisplayName}' --pr '17' --slot 2 "
                    + $"--db '{run.Connection.DataSource}' --session 'session' --workspace 'workspace' "
                    + $"--provider 'test' --mode '{mode ?? "workspace-agent"}'"
                    + (effort is null ? "" : $" --reasoning-effort '{effort}'")
                    + $" --head '{new string('a', 40)}' --resume --execute",
                "the resume repeats the attempt's own options"
            );
        run.Phases.Should()
            .NotContain(
                phase => phase.StartsWith("sandbox-release", StringComparison.Ordinal),
                "nothing is released before the final resume"
            );
        printed["shell"]!.GetValue<string>().Should().Be("PowerShell");
        printed["fill"].Should().BeNull("nothing is left to fill in");
        run.Phases.Clear();
        var resume = ReviewdCommandLines.PowerShellArguments(next);
        resume[0].Should().Be("reviewd");

        (await run.RunAsync(resume[1..], CancellationToken.None)).Should().Be(0);

        // The finished review releases its own thread's sandbox once, after its cleanup, and moves nothing.
        var release = ScriptedReviewRun.SandboxRelease("thread", null);
        run.Phases.Where(phase => phase.StartsWith("sandbox-release", StringComparison.Ordinal))
            .Should()
            .Equal(release);
        run.Phases.Should().ContainInOrder("inspect-prepared", "verify", "commit", "cleanup", release);
        var receipt = await run.ReadAsync(run.RunKey);
        var attempt = await run.ReadAsync(run.AttemptKey);
        var entry = new JsonObject { ["head"] = new string('a', 40) };
        ReviewBatch.Verified(receipt, entry).Should().BeTrue("the resume wrote its own receipt");
        ReviewBatch.Verified(attempt, entry).Should().BeTrue();
        ReviewBatch.Reusable(attempt).Should().BeTrue("the batch reads it as verified, not held");
        (await run.ReadAsync(run.OwnerKey))["state"]!.GetValue<string>().Should().Be("released");
    }

    // An operator discard of that held review is refused before it touches anything: the discard resets the slot, after
    // which the review could never be verified, so the command names finish-commit instead. The review then finishes.
    [Fact]
    public async Task A_discard_of_a_review_whose_commit_landed_is_refused_and_names_finish_commit()
    {
        await using var run = await ScriptedReviewRun.CreateAsync();
        var killed = await KillAfterCommitAsync(run);
        var before = killed.ToJsonString();
        var owner = (await run.ReadAsync(run.OwnerKey)).ToJsonString();
        var handler = new FakeHttpMessageHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var sandbox = new List<string>();
        var discard = () =>
            ReclaimSlotCommand.RunAsync(
                ReviewdCommandLines.ReclaimSlot(
                    ScriptedReviewRun.Repo.DisplayName,
                    "17",
                    2,
                    run.Database,
                    "--discard-verified",
                    "--execute"
                ),
                () => new LmStreamingS2SClient(http, null, null, null),
                () =>
                    (phase, _, _, _) =>
                    {
                        sandbox.Add(phase);
                        return Task.FromResult(new JsonObject());
                    },
                CancellationToken.None
            );

        await discard
            .Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("This review's commit landed; run `reviewd review finish-commit *` to finish it*strand it*");

        handler.Requests.Should().BeEmpty("nothing is frozen");
        sandbox.Should().BeEmpty("nothing is reset");
        (await run.ReadAsync(run.AttemptKey)).ToJsonString().Should().Be(before, "the review stays finishable");
        (await run.ReadAsync(run.OwnerKey)).ToJsonString().Should().Be(owner, "the slot stays held");
        (await FinishCommitAsync(run, execute: true)).Exit.Should().Be(0);
    }

    // Each case the command cannot finish safely is refused with the operator's next step, and neither the attempt nor
    // the owner row is changed. An attempt on another slot or workspace is refused even though the requested slot's
    // owner is valid and held by its claim. A preview checks the same rules and changes nothing either.
    [Theory]
    [InlineData("released", "*already released*new push gets a fresh review*")]
    [InlineData("stage", "Only a commit-completed attempt can be finished; this one is at commit-pending.")]
    [InlineData("no-commit", "The attempt has no landed artifact commit; nothing to finish.")]
    [InlineData("wrong-slot", "The attempt's slot or workspace does not match the request.")]
    [InlineData("wrong-workspace", "The attempt's slot or workspace does not match the request.")]
    [InlineData("other-owner", "Slot 2 is not owned by this attempt's claim*")]
    [InlineData("absent-owner", "Slot 2 is not owned by this attempt's claim*")]
    [InlineData("released-owner", "Slot 2 is not owned by this attempt's claim*")]
    [InlineData("fenced", "A reset fenced slot 2*`reviewd review reclaim-slot *--discard-verified --execute`.")]
    [InlineData("live-run", "A review run still holds slot 2; stop it before finishing its commit.")]
    [InlineData("preview", null)]
    public async Task Finish_commit_refuses_what_it_cannot_finish_safely(string state, string? refusal)
    {
        await using var run = await ScriptedReviewRun.CreateAsync();
        _ = await SlotRelease.ClaimAsync(
            run.Connection,
            run.OwnerKey,
            new JsonObject
            {
                ["repository"] = ScriptedReviewRun.Repo.DisplayName,
                ["prId"] = "17",
                ["slot"] = 2,
                ["workspaceId"] = "workspace",
                ["sessionId"] = "session",
                ["headSha"] = new string('a', 40),
            },
            null,
            run.AttemptKey,
            CancellationToken.None
        );
        var attempt = await run.ReadAsync(run.AttemptKey);
        attempt["stage"] = state == "stage" ? "commit-pending" : "commit-completed";
        attempt["artifactCommit"] = state == "no-commit" ? null : ScriptedReviewRun.Committed();
        if (state == "released")
        {
            attempt["cleanupState"] = "released";
        }
        if (state == "wrong-slot")
        {
            attempt["slot"] = 3;
        }
        if (state == "wrong-workspace")
        {
            attempt["workspaceId"] = "other-workspace";
        }
        await run.Store.SetAsync(run.AttemptKey, attempt);
        var owner = await run.ReadAsync(run.OwnerKey);
        if (state == "other-owner")
        {
            owner["claimToken"] = "other";
        }
        if (state == "released-owner")
        {
            owner["state"] = "released";
        }
        if (state == "fenced")
        {
            owner[SlotRelease.ResetFence] = "other";
        }
        await run.Store.SetAsync(run.OwnerKey, owner);
        if (state == "absent-owner")
        {
            await run.ExecuteAsync($"DELETE FROM cache WHERE key = '{run.OwnerKey}';");
        }
        var before = (await run.ReadAsync(run.AttemptKey)).ToJsonString();
        var ownerBefore = (await run.Store.GetAsync<JsonObject>(run.OwnerKey))?.ToJsonString();
        using var liveRun =
            state == "live-run"
                ? ReviewRunner.AcquireLease(
                    run.Database,
                    ReviewRunner.SlotLeaseIdentity("workspace", ScriptedReviewRun.Repo, 2)
                )
                : null;

        var finish = () => FinishCommitAsync(run, execute: state != "preview");

        if (refusal is null)
        {
            (await finish()).Exit.Should().Be(0);
        }
        else
        {
            await finish.Should().ThrowAsync<InvalidOperationException>().WithMessage(refusal);
        }
        (await run.ReadAsync(run.AttemptKey)).ToJsonString().Should().Be(before, "nothing is changed");
        (await run.Store.GetAsync<JsonObject>(run.OwnerKey))?.ToJsonString().Should().Be(ownerBefore, "nor the owner");
    }

    // An Azure DevOps project or repository name may hold spaces, and a value may hold a quote. Each printed value stays
    // one PowerShell argument, in the resume and in the discard a fenced slot names.
    [Fact]
    public async Task Each_printed_value_stays_one_PowerShell_argument()
    {
        await using var run = await ScriptedReviewRun.CreateAsync();
        var repo = PrCommands.ParseRepo("org/My Project/Nova\u2019s repo");
        var ownerKey = await SeedFinishableAsync(
            run,
            repo,
            "work space",
            new JsonObject
            {
                ["sessionId"] = "it's a session",
                ["providerId"] = "test",
                ["mode"] = "review mode",
                ["reasoningEffort"] = "high",
            }
        );

        var result = await FinishCommitCommand.FinishAsync(
            run.Connection,
            repo,
            "17",
            2,
            "work space",
            execute: false,
            CancellationToken.None
        );

        ReviewdCommandLines
            .PowerShellArguments(result["next"]!.GetValue<string>())
            .Should()
            .Equal(
                "reviewd",
                "review",
                "run",
                "--repo",
                "org/My Project/Nova\u2019s repo",
                "--pr",
                "17",
                "--slot",
                "2",
                "--db",
                run.Connection.DataSource,
                "--session",
                "it's a session",
                "--workspace",
                "work space",
                "--provider",
                "test",
                "--mode",
                "review mode",
                "--reasoning-effort",
                "high",
                "--head",
                new string('a', 40),
                "--resume",
                "--execute"
            );
        var owner = await run.ReadAsync(ownerKey);
        owner[SlotRelease.ResetFence] = "stopped-reset";
        await run.Store.SetAsync(ownerKey, owner);
        var refusal = await FluentActions
            .Invoking(() =>
                FinishCommitCommand.FinishAsync(
                    run.Connection,
                    repo,
                    "17",
                    2,
                    "work space",
                    execute: false,
                    CancellationToken.None
                )
            )
            .Should()
            .ThrowAsync<InvalidOperationException>();
        var discard = refusal.Which.Message.Split('`')[1];
        ReviewdCommandLines
            .PowerShellArguments(discard.Replace("<session>", "session", StringComparison.Ordinal))
            .Should()
            .ContainInConsecutiveOrder("--repo", "org/My Project/Nova\u2019s repo")
            .And.ContainInConsecutiveOrder("--workspace", "work space")
            .And.ContainInConsecutiveOrder("--db", run.Connection.DataSource);
    }

    // What the attempt did not save is left as a placeholder PowerShell refuses to run, and listed under fill, never
    // guessed: an attempt from before attempts saved their mode, and a shadow arm's manifest and task prompt files. A
    // shadow attempt gets its shadow options, since its database refuses a live run; its mode is in its provenance.
    [Theory]
    [InlineData("legacy", "--mode <mode> --head", "<mode>: this attempt predates saved review modes*")]
    [InlineData(
        "shadow",
        "--mode 'arm-mode' --shadow --post-session 'post' --variant 'v1' --manifest <manifest file> "
            + "--experiment 'exp-1' --task-prompt-file <task prompt file> --head",
        "<manifest file>: the variant manifest this arm ran (SHA-256 m1).|<task prompt file>: *(SHA-256 t1)."
    )]
    [InlineData("shadow-default-prompt", "--mode 'arm-mode' --shadow --post-session 'post' --head", null)]
    public async Task A_resume_leaves_what_the_attempt_did_not_save_to_fill_in(
        string attemptKind,
        string expected,
        string? fill
    )
    {
        await using var run = await ScriptedReviewRun.CreateAsync();
        var fields = new JsonObject { ["sessionId"] = "session", ["providerId"] = "test" };
        if (attemptKind != "legacy")
        {
            fields["shadow"] = true;
            fields["postSessionId"] = "post";
            fields["provenance"] = new JsonObject
            {
                ["mode"] = "arm-mode",
                ["taskPromptSha256"] =
                    attemptKind == "shadow" ? "t1" : VariantManifest.Sha256Hex(ShadowTaskPrompt.Default),
            };
        }
        if (attemptKind == "shadow")
        {
            fields["variant"] = "v1";
            fields["provenance"]!["manifestSha256"] = "m1";
            fields["provenance"]!["experimentId"] = "exp-1";
        }
        _ = await SeedFinishableAsync(run, ScriptedReviewRun.Repo, "workspace", fields);

        var result = await FinishCommitCommand.FinishAsync(
            run.Connection,
            ScriptedReviewRun.Repo,
            "17",
            2,
            "workspace",
            execute: false,
            CancellationToken.None
        );

        var next = result["next"]!.GetValue<string>();
        next.Should().Contain($"--provider 'test' {expected} '{new string('a', 40)}' --resume --execute");
        if (fill is null)
        {
            result["fill"].Should().BeNull();
            _ = ReviewdCommandLines.PowerShellArguments(next);
            return;
        }
        var expectedFill = fill.Split('|');
        var printedFill = result["fill"]!.AsArray().Select(step => step!.GetValue<string>()).ToList();
        printedFill.Should().HaveCount(expectedFill.Length);
        for (var i = 0; i < expectedFill.Length; i++)
        {
            printedFill[i].Should().Match(expectedFill[i]);
        }
        FluentActions
            .Invoking(() => ReviewdCommandLines.PowerShellArguments(next))
            .Should()
            .Throw<ArgumentException>("PowerShell refuses a placeholder");
    }

    // A commit-completed attempt with a landed commit, owned by its own claim on slot 2 of the workspace, with the given
    // saved fields in place of a run's. Returns the owner row's key.
    private static async Task<string> SeedFinishableAsync(
        ScriptedReviewRun run,
        RepoIdentity repo,
        string workspace,
        JsonObject fields
    )
    {
        var attemptKey = $"reviewd/review-attempt/{repo.NormalizedKey}/17";
        var ownerKey = SlotRelease.OwnerKey(workspace, repo.NormalizedKey, 2);
        _ = await SlotRelease.ClaimAsync(
            run.Connection,
            ownerKey,
            new JsonObject
            {
                ["repository"] = repo.DisplayName,
                ["prId"] = "17",
                ["slot"] = 2,
                ["workspaceId"] = workspace,
                ["headSha"] = new string('a', 40),
            },
            null,
            attemptKey,
            CancellationToken.None
        );
        var attempt = await run.ReadAsync(attemptKey);
        attempt["stage"] = "commit-completed";
        attempt["artifactCommit"] = ScriptedReviewRun.Committed();
        foreach (var (name, value) in fields)
        {
            attempt[name] = value?.DeepClone();
        }
        await run.Store.SetAsync(attemptKey, attempt);
        return ownerKey;
    }

    // The process dies once the commit landed: its commit-completed save is its last write, so the attempt keeps the
    // slot and the run never writes its receipt.
    private static async Task<JsonObject> KillAfterCommitAsync(ScriptedReviewRun run)
    {
        run.OnPhase = (phase, _, _, _) =>
        {
            if (phase == "commit")
            {
                run.Cancellation.Cancel();
            }
            return Task.FromResult<JsonObject?>(null);
        };
        await FluentActions.Invoking(() => run.RunAsync()).Should().ThrowAsync<OperationCanceledException>();
        run.OnPhase = null;
        return await run.ReadAsync(run.AttemptKey);
    }

    // The command as the CLI runs it: its exit code and the JSON it printed (null when it printed none).
    private static async Task<(int Exit, JsonObject? Printed)> FinishCommitAsync(ScriptedReviewRun run, bool execute)
    {
        var stdout = Console.Out;
        using var printed = new StringWriter();
        Console.SetOut(printed);
        int exit;
        try
        {
            exit = await FinishCommitCommand.RunAsync(
                CliOptions.Parse([
                    "review",
                    "finish-commit",
                    "--repo",
                    ScriptedReviewRun.Repo.DisplayName,
                    "--pr",
                    "17",
                    "--slot",
                    "2",
                    "--db",
                    run.Database,
                    "--workspace",
                    "workspace",
                    .. execute ? new[] { "--execute" } : [],
                ]),
                CancellationToken.None
            );
        }
        finally
        {
            Console.SetOut(stdout);
        }
        return (exit, printed.ToString() is { Length: > 0 } text ? JsonNode.Parse(text)!.AsObject() : null);
    }
}
