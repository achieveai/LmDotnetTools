using System.Text.Json;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using CodeReviewDaemon.Sample.Workspace;
using CodeReviewDaemon.Sample.Workspace.Sandbox;

namespace CodeReviewDaemon.Sample.Tests.Workspace;

public sealed class ReviewSetupScriptRunnerTests
{
    [Fact]
    public async Task Preparation_preflights_warm_slot_then_runs_setup_and_verifies_exact_merge_provenance()
    {
        var commands = new ScriptRunner();
        var runner = new ReviewSetupScriptRunner(commands, Assets());
        var checkout = await runner.PrepareAsync(Run(), new("Nova", 2), Repository(), default);
        commands.Commands.Should().HaveCount(4);
        commands.Commands[0].Argv.Should().Contain(arg => arg.EndsWith("review_reset.py", StringComparison.Ordinal));
        commands.Commands[0].Argv.Should().Contain("--strict").And.Contain("discard");
        commands.Commands[1].Argv.Should().Contain(ReviewSetupScriptRunner.EvidenceScript);
        commands.Commands[1].Argv[^1].Should().Contain("\"pr\":null");
        commands.Commands[2].Argv.Should().Contain(ReviewSetupScriptRunner.SetupScript);
        commands.Commands[3].Argv.Should().Contain(ReviewSetupScriptRunner.EvidenceScript);
        commands.Commands[3].Argv[^1].Should().Contain(Run().HeadSha).And.Contain(Run().BaseSha);
        commands.Commands.Should().OnlyContain(c => c.WorkingDirectory == "/workspace");
        checkout.CheckoutSha.Should().Be(new string('c', 40));
        checkout.SourceHeadSha.Should().Be(Run().HeadSha);
        checkout.TargetBaseSha.Should().Be(Run().BaseSha);
        checkout.TargetDir.Should().Be("/workspace/.worktrees/Nova-2/repos/Nova");
    }

    [Fact]
    public async Task Preparation_accepts_an_already_idle_warm_slot_before_setup()
    {
        var commands = new ScriptRunner();
        var runner = new ReviewSetupScriptRunner(commands, Assets());

        var checkout = await runner.PrepareAsync(Run(), new("Nova", 2), Repository(), default);

        commands.Commands.Should().HaveCount(4);
        commands.Commands[0].Argv.Should().Contain(arg => arg.EndsWith("review_reset.py", StringComparison.Ordinal));
        commands.Commands[1].Argv.Should().Contain(ReviewSetupScriptRunner.EvidenceScript);
        commands.Commands[2].Argv.Should().Contain(ReviewSetupScriptRunner.SetupScript);
        commands.Commands[3].Argv.Should().Contain(ReviewSetupScriptRunner.EvidenceScript);
        checkout.TargetDir.Should().Be("/workspace/.worktrees/Nova-2/repos/Nova");
    }

    [Fact]
    public async Task Preparation_rejects_failed_warm_preflight_before_setup()
    {
        var commands = new ScriptRunner { WarmEvidenceExit = 1 };
        var runner = new ReviewSetupScriptRunner(commands, Assets());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runner.PrepareAsync(Run(), new("Nova", 2), Repository(), default)
        );

        commands.Commands.Should().HaveCount(2);
        commands.Commands[0].Argv.Should().Contain(arg => arg.EndsWith("review_reset.py", StringComparison.Ordinal));
        commands.Commands[1].Argv.Should().Contain(ReviewSetupScriptRunner.EvidenceScript);
    }

    [Fact]
    public async Task Existing_slot_evidence_requires_relative_outer_and_source_links_without_repairing()
    {
        var commands = new ScriptRunner();
        var runner = new ReviewSetupScriptRunner(commands, Assets());
        await runner.VerifyWarmAsync([new("Nova", 0)], default);
        var script = commands.Commands.Single(c => c.Argv.Contains(ReviewSetupScriptRunner.EvidenceScript)).Argv[2];
        script.Should().Contain("validate_relative_worktree(root, outer, state.common)");
        script.Should().Contain("validate_relative_worktree(root, source, pool.store)");
        script.Should().NotContain("require_relative=False").And.NotContain("ensure_relative_worktrees");
        commands.Commands.Should().NotContain(c => c.Argv.Contains("repair") || c.Argv.Contains("warm"));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Setup_context_warning_and_moved_PR_are_distinguished(int code)
    {
        var commands = new ScriptRunner { SetupExit = code };
        var runner = new ReviewSetupScriptRunner(commands, Assets());
        if (code == 3)
            await Assert.ThrowsAsync<ReviewPullRequestMovedException>(() =>
                runner.PrepareAsync(Run(), new("Nova", 2), Repository(), default)
            );
        else
            (await runner.PrepareAsync(Run(), new("Nova", 2), Repository(), default)).ContextWarning.Should().BeTrue();
    }

    [Fact]
    public async Task Lost_remote_wait_is_unknown_not_a_definite_failure()
    {
        var commands = new ScriptRunner { LoseSetupWait = true };
        var runner = new ReviewSetupScriptRunner(commands, Assets());
        await Assert.ThrowsAsync<RemoteWorkspaceOutcomeUnknownException>(() =>
            runner.PrepareAsync(Run(), new("Nova", 2), Repository(), default)
        );
    }

    [Fact]
    public async Task Missing_LLM_managed_assets_fail_before_remote_mutation()
    {
        var commands = new ScriptRunner();
        var runner = new ReviewSetupScriptRunner(commands, new FakeSandboxFileSystem());
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runner.PrepareAsync(Run(), new("Nova", 2), Repository(), default)
        );
        commands.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task Unsupported_source_provider_fails_before_setup_but_does_not_change_outer_store_contract()
    {
        var commands = new ScriptRunner();
        var runner = new ReviewSetupScriptRunner(commands, Assets());
        var github = new RepoIdentity
        {
            Provider = "github",
            OrgOrOwner = "example",
            RepoName = "Nova",
        };
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            runner.PrepareAsync(Run(), new("Nova", 2), github, default)
        );
        commands.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task Foreign_slot_evidence_is_not_accepted()
    {
        var commands = new ScriptRunner { EvidenceRepository = "Foreign" };
        var runner = new ReviewSetupScriptRunner(commands, Assets());
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            runner.PrepareAsync(Run(), new("Nova", 2), Repository(), default)
        );
    }

    [Theory]
    [InlineData("", 0)]
    [InlineData("{\"schema\":1,\"mode\":\"discard\",\"strict\":true,\"status\":\"discarded\",\"slot\":\"Nova-1\"}", 0)]
    [InlineData("{}", 1)]
    public async Task Strict_discard_refuses_missing_foreign_or_failed_receipt(string receipt, int exitCode)
    {
        var commands = new ScriptRunner { ResetReceipt = receipt, ResetExit = exitCode };
        var runner = new ReviewSetupScriptRunner(commands, Assets());
        await Assert.ThrowsAnyAsync<Exception>(() => runner.PrepareAsync(Run(), new("Nova", 2), Repository(), default));
        commands.Commands.Should().ContainSingle();
        commands.Commands[0].Argv.Should().Contain("--strict");
    }

    [Fact]
    public async Task Explicit_debug_mode_preserves_warm_slot_without_discard()
    {
        var commands = new ScriptRunner();
        var runner = new ReviewSetupScriptRunner(commands, Assets(), autoDiscardCompletedSlotOnAdmission: false);
        await runner.PrepareAsync(Run(), new("Nova", 2), Repository(), default);
        commands.Commands.Should().HaveCount(3);
        commands
            .Commands.Should()
            .NotContain(command => command.Argv.Any(arg => arg.EndsWith("review_reset.py", StringComparison.Ordinal)));
    }

    private const string HistoryProgress =
        "Fetch progress log: /workspace/.git/review-setup/progress.log\n"
        + "Effective aggregate history timeout: 3600s (--history-timeout); background tool timeout is separate.\n";

    [Theory]
    [InlineData("{\"complete\":true}")]
    [InlineData(HistoryProgress + "{\n  \"complete\": true,\n  \"covered_depth\": 38134\n}\n")]
    [InlineData(" \r\n" + HistoryProgress + " \n{\"complete\":true}\r\n")]
    public async Task History_priming_accepts_complete_JSON_with_only_known_progress_preamble(string stdout)
    {
        var runner = new ReviewSetupScriptRunner(new ScriptRunner { HistoryStdout = stdout }, Assets());
        await runner.PrimeFullHistoryAsync(new("Nova", 0), default);
    }

    [Theory]
    [InlineData("unknown progress\n{\"complete\":true}")]
    [InlineData(HistoryProgress + "{\"complete\":true")]
    [InlineData(HistoryProgress + "{\"complete\":true}\ntrailing garbage")]
    [InlineData(HistoryProgress + "{\"complete\":true}\n{\"complete\":true}")]
    [InlineData("Fetch progress log: /workspace/log\n{\"complete\":true}")]
    [InlineData("Fetch progress log: \n{\"complete\":true}")]
    [InlineData(HistoryProgress)]
    public async Task History_priming_rejects_unknown_malformed_or_truncated_output(string stdout)
    {
        var runner = new ReviewSetupScriptRunner(new ScriptRunner { HistoryStdout = stdout }, Assets());
        await Assert.ThrowsAsync<JsonException>(() => runner.PrimeFullHistoryAsync(new("Nova", 0), default));
    }

    [Theory]
    [InlineData("{\"complete\":false}")]
    [InlineData(HistoryProgress + "{\"complete\":false}")]
    [InlineData(HistoryProgress + "{}")]
    [InlineData(HistoryProgress + "null")]
    public async Task History_priming_requires_positive_complete_ancestry_evidence(string stdout)
    {
        var runner = new ReviewSetupScriptRunner(new ScriptRunner { HistoryStdout = stdout }, Assets());
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runner.PrimeFullHistoryAsync(new("Nova", 0), default)
        );
    }

    [Theory]
    [InlineData(true, 1, "ready", true)]
    [InlineData(false, 1, "ready", false)]
    [InlineData(true, 2, "ready", false)]
    [InlineData(true, 1, "planned", false)]
    public async Task Root_setup_requires_complete_structured_results(
        bool complete,
        int count,
        string status,
        bool accepted
    )
    {
        var commands = new ScriptRunner
        {
            RootStdout = JsonSerializer.Serialize(
                new
                {
                    schema = 1,
                    mode = "apply",
                    complete,
                    moduleCount = count,
                    repositories = new[]
                    {
                        new
                        {
                            repository = "Nova",
                            path = "repos/Nova",
                            branch = "dev",
                            targetSha = new string('a', 40),
                            status,
                        },
                    },
                }
            ),
        };
        var files = Assets();
        files.Files[$"/workspace/{ReviewSetupScriptRunner.RootSetupScript}"] = "# installed";
        var runner = new ReviewSetupScriptRunner(commands, files);
        if (accepted)
            await runner.SetupRootRepositoriesAsync(default);
        else
            await Assert.ThrowsAsync<InvalidDataException>(() => runner.SetupRootRepositoriesAsync(default));
        commands.Commands.Should().ContainSingle(c => c.Argv.Contains("apply") && c.WorkingDirectory == "/workspace");
    }

    [Fact]
    public async Task Missing_root_setup_asset_fails_before_remote_execution()
    {
        var commands = new ScriptRunner();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new ReviewSetupScriptRunner(commands, Assets()).SetupRootRepositoriesAsync(default)
        );
        commands.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task Root_cleanup_uncertainty_is_not_a_definite_exit_failure()
    {
        var files = Assets();
        files.Files[$"/workspace/{ReviewSetupScriptRunner.RootSetupScript}"] = "# installed";
        var commands = new ScriptRunner { RootExit = 75 };
        await Assert.ThrowsAsync<RemoteWorkspaceOutcomeUnknownException>(() =>
            new ReviewSetupScriptRunner(commands, files).SetupRootRepositoriesAsync(default, TimeSpan.FromHours(4))
        );
        commands.Commands.Single(c => c.Argv.Contains("apply")).Argv.Should().Contain("14400");
    }

    [Fact]
    public async Task Root_completion_cannot_omit_an_authoritatively_declared_repository()
    {
        var commands = new ScriptRunner
        {
            InventoryStdout =
                "{\"schema\":1,\"repositories\":[{\"repository\":\"Nova\",\"path\":\"repos/Nova\"},{\"repository\":\"Astra\",\"path\":\"repos/Astra\"}]}",
            RootStdout = JsonSerializer.Serialize(
                new
                {
                    schema = 1,
                    mode = "apply",
                    complete = true,
                    moduleCount = 1,
                    repositories = new[]
                    {
                        new
                        {
                            repository = "Nova",
                            path = "repos/Nova",
                            branch = "dev",
                            targetSha = new string('a', 40),
                            status = "ready",
                        },
                    },
                }
            ),
        };
        var files = Assets();
        files.Files[$"/workspace/{ReviewSetupScriptRunner.RootSetupScript}"] = "# installed";
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new ReviewSetupScriptRunner(commands, files).SetupRootRepositoriesAsync(default)
        );
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Existing_slots_are_verified_and_only_absent_slots_are_warmed(bool absent)
    {
        var commands = new ScriptRunner { SlotAbsent = absent };
        await new ReviewSetupScriptRunner(commands, Assets()).EnsureWarmAsync(new("Nova", 0), default);
        commands.Commands.Any(c => c.Argv.Contains("warm")).Should().Be(absent);
        commands.Commands.Any(c => c.Argv.Contains(ReviewSetupScriptRunner.EvidenceScript)).Should().Be(!absent);
    }

    [Fact]
    public async Task Existing_foreign_slot_fails_without_warming_or_resetting()
    {
        var commands = new ScriptRunner { EvidenceRepository = "Foreign" };
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new ReviewSetupScriptRunner(commands, Assets()).EnsureWarmAsync(new("Nova", 0), default)
        );
        commands.Commands.Should().NotContain(c => c.Argv.Contains("warm") || c.Argv.Contains("reset"));
    }

    private static RepoIdentity Repository() =>
        new()
        {
            Provider = "ado",
            OrgOrOwner = "example",
            Project = "project",
            RepoName = "Nova",
        };

    private static ReviewRun Run() =>
        new()
        {
            RepoId = 1,
            PrId = "7",
            HeadSha = new string('a', 40),
            BaseSha = new string('b', 40),
            TriggerWatermark = "w",
            ReviewKind = "full",
            VariantId = "primary",
            Mode = "collect-only",
            Stage = ReviewStage.Discovered,
            WorkflowStatus = WorkflowStatus.Pending,
            PrLifecycleState = PrLifecycleState.Open,
        };

    private static FakeSandboxFileSystem Assets()
    {
        var files = new FakeSandboxFileSystem();
        foreach (
            var file in new[]
            {
                "review_pool_admin.py",
                "review_common.py",
                "review_reset.py",
                "review_setup.py",
                "review_git_prepare.py",
            }
        )
            files.Files[$"/workspace/{ReviewSetupScriptRunner.ScriptsRelativePath}/{file}"] = "# LLM installed";
        return files;
    }

    private sealed class ScriptRunner : ISandboxCommandRunner
    {
        public int SetupExit { get; init; }
        public int WarmEvidenceExit { get; init; }
        public bool LoseSetupWait { get; init; }
        public string HistoryStdout { get; init; } = "{\"complete\":true}";
        public string RootStdout { get; init; } = "{}";
        public int RootExit { get; init; }
        public bool SlotAbsent { get; init; }
        public string InventoryStdout { get; init; } =
            "{\"schema\":1,\"repositories\":[{\"repository\":\"Nova\",\"path\":\"repos/Nova\"}]}";
        public string EvidenceRepository { get; init; } = "Nova";
        public string ResetReceipt { get; init; } =
            "{\"schema\":1,\"mode\":\"discard\",\"strict\":true,\"status\":\"already_idle\",\"slot\":\"Nova-2\"}";
        public int ResetExit { get; init; }
        public List<SandboxCommand> Commands { get; } = [];

        public Task<SandboxCommandResult> RunAsync(SandboxCommand command, CancellationToken cancellationToken)
        {
            Commands.Add(command);
            if (command.Argv.Any(arg => arg.EndsWith("review_reset.py", StringComparison.Ordinal)))
                return Task.FromResult(new SandboxCommandResult(ResetExit, ResetReceipt, ""));
            if (command.Argv.Contains(ReviewSetupScriptRunner.SlotPresenceScript))
                return Task.FromResult(
                    new SandboxCommandResult(0, JsonSerializer.Serialize(new { absent = SlotAbsent }), "")
                );
            if (command.Argv.Contains("inventory"))
                return Task.FromResult(new SandboxCommandResult(0, InventoryStdout, ""));
            if (command.Argv.Contains(ReviewSetupScriptRunner.RootSetupScript))
                return Task.FromResult(new SandboxCommandResult(RootExit, RootStdout, ""));
            if (command.Argv.Contains("prime-history"))
                return Task.FromResult(new SandboxCommandResult(0, HistoryStdout, ""));
            if (command.Argv.Contains(ReviewSetupScriptRunner.SetupScript))
            {
                if (LoseSetupWait)
                    throw new OperationCanceledException("local wait ended");
                return Task.FromResult(new SandboxCommandResult(SetupExit, "", ""));
            }
            if (!command.Argv.Contains(ReviewSetupScriptRunner.EvidenceScript))
                return Task.FromResult(new SandboxCommandResult(0, "", ""));
            using var request = JsonDocument.Parse(command.Argv[^1]);
            var slot = request.RootElement.GetProperty("slot").GetInt32();
            if (request.RootElement.GetProperty("pr").ValueKind == JsonValueKind.Null && WarmEvidenceExit != 0)
                return Task.FromResult(new SandboxCommandResult(WarmEvidenceExit, "", "warm slot unavailable"));
            var body = JsonSerializer.Serialize(
                new
                {
                    repository = EvidenceRepository,
                    slot,
                    sourcePath = $".worktrees/Nova-{slot}/repos/Nova",
                    sourceStore = "review-setup/source-stores/nova.git",
                    checkoutSha = new string('c', 40),
                    mergeBaseSha = new string('b', 40),
                    preparedManifest = ".git/review-setup/prepared-slots/nova-2.json",
                }
            );
            return Task.FromResult(new SandboxCommandResult(0, body, ""));
        }
    }
}
