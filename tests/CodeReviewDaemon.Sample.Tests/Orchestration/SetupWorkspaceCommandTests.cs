using System.Text.Json;
using CodeReviewDaemon.Sample.Configuration;
using CodeReviewDaemon.Sample.Orchestration;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using CodeReviewDaemon.Sample.Workspace;
using CodeReviewDaemon.Sample.Workspace.Sandbox;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeReviewDaemon.Sample.Tests.Orchestration;

public sealed class SetupWorkspaceCommandTests
{
    [Fact]
    public async Task All_repository_slots_are_warmed_once_in_one_shared_session_and_reused_on_rerun()
    {
        var pool = Pool();
        var runner = new SetupRunner();
        var sessions = Sessions(runner);
        await Run(pool, sessions);
        var firstRunCount = runner.Commands.Count;
        await Run(pool, sessions);
        runner
            .Commands.Skip(firstRunCount)
            .Should()
            .NotContain(c => c.Argv.Contains("prime-history") || c.Argv.Contains("warm"));
        runner.Commands.Count(c => c.Argv.Contains("prime-history")).Should().Be(4);
        runner
            .Commands.Count(c => c.Argv.Contains(ReviewSetupScriptRunner.RootSetupScript) && c.Argv.Contains("apply"))
            .Should()
            .Be(2);
        runner
            .Commands.FindIndex(c => c.Argv.Contains(ReviewSetupScriptRunner.RootSetupScript))
            .Should()
            .BeLessThan(runner.Commands.FindIndex(c => c.Argv.Contains("prime-history")));
        runner
            .Commands.Where(c => c.Argv.Contains("warm") && c.Argv[1] != "-c")
            .Select(c => string.Join(' ', c.Argv))
            .Distinct()
            .Should()
            .HaveCount(4);
        runner.Commands.Count(c => c.Argv.Contains("warm") && c.Argv[1] != "-c").Should().Be(4);
        sessions.AcquisitionCount.Should().Be(2);
        foreach (var slot in pool.Slots)
            (await pool.TryLeasePreferredAsync(slot, default)).Should().Be(slot);
    }

    [Fact]
    public async Task One_setup_invocation_prepares_eighteen_configured_slots_without_duplicate_addresses()
    {
        var pool = new ReviewSlotPool(
            ["Nova", "NovaClient", "Astra", "WeveNova", "MODISService"],
            3,
            NullLogger<ReviewSlotPool>.Instance,
            new Dictionary<string, int> { ["Nova"] = 6 }
        );
        var runner = new SetupRunner();
        await Run(pool, Sessions(runner));
        runner.Commands.Count(c => c.Argv.Contains("apply")).Should().Be(1);
        runner.Commands.Where(c => c.Argv.Contains("warm")).Should().HaveCount(18);
        runner
            .Commands.Where(c => c.Argv.Contains("warm"))
            .Select(c => string.Join(' ', c.Argv))
            .Should()
            .OnlyHaveUniqueItems();
        var before = runner.Commands.Count;
        await Run(pool, Sessions(runner));
        runner
            .Commands.Skip(before)
            .Should()
            .NotContain(c => c.Argv.Contains("warm") || c.Argv.Contains("prime-history"));
    }

    [Theory]
    [InlineData("prime-history")]
    [InlineData(ReviewSetupScriptRunner.RootSetupScript)]
    [InlineData("warm")]
    public async Task Unknown_remote_outcome_quarantines_the_entire_shared_batch(string operation)
    {
        var pool = Pool();
        var runner = new SetupRunner { UnknownOperation = operation };
        await Assert.ThrowsAsync<RemoteWorkspaceOutcomeUnknownException>(() => Run(pool, Sessions(runner)));
        (await pool.TryLeaseAsync("Nova", default)).Should().BeNull();
        (await pool.TryLeaseAsync("Widgets", default)).Should().BeNull();
    }

    [Fact]
    public async Task Definite_failure_propagates_and_does_not_log_remote_payload()
    {
        var pool = Pool();
        var runner = new SetupRunner { FailedOperation = "warm" };
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(pool, Sessions(runner)));
        exception.Message.Should().NotContain("secret-stderr");
        (await pool.TryLeaseAsync("Nova", default)).Should().NotBeNull();
    }

    [Fact]
    public async Task Incomplete_history_is_not_accepted_as_success()
    {
        var runner = new SetupRunner { HistoryComplete = false };
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(Pool(), Sessions(runner)));
        runner.Commands.Should().NotContain(c => c.Argv.Contains("warm"));
    }

    [Fact]
    public void Setup_refuses_polling_race() =>
        Assert.Throws<InvalidOperationException>(() =>
            SetupWorkspaceCommand.EnsureCanRunWithoutPolling(new CodeReviewDaemonOptions { EnablePrPolling = true })
        );

    [Fact]
    public void Setup_accepts_disabled_polling() =>
        SetupWorkspaceCommand.EnsureCanRunWithoutPolling(new CodeReviewDaemonOptions { EnablePrPolling = false });

    private static ReviewSlotPool Pool() => new(["Nova", "Widgets"], 2, NullLogger<ReviewSlotPool>.Instance);

    private static async Task Run(ReviewSlotPool pool, FakeReviewSessionProvisioner sessions)
    {
        using var db = new TempSqliteDatabase();
        using var store = new CodeReviewDaemon.Sample.Persistence.ReviewStore(db.ConnectionString);
        await SetupWorkspaceCommand.RunAsync(
            pool,
            sessions,
            "https://example.invalid/store.git",
            ["--setup-workspace"],
            "/irrelevant",
            NullLogger.Instance,
            default,
            store
        );
    }

    [Fact]
    public async Task Bootstrap_uncertainty_blocks_rerun_even_with_a_new_pool()
    {
        using var db = new TempSqliteDatabase();
        using var store = new CodeReviewDaemon.Sample.Persistence.ReviewStore(db.ConnectionString);
        var sessions = Sessions(new SetupRunner { UnknownOperation = "prime-history" });
        await Assert.ThrowsAsync<RemoteWorkspaceOutcomeUnknownException>(() =>
            SetupWorkspaceCommand.RunAsync(
                Pool(),
                sessions,
                "https://example.invalid/store.git",
                [],
                "/irrelevant",
                NullLogger.Instance,
                default,
                store
            )
        );
        store.HasUnsettledWorkspaceBootstrap().Should().BeTrue();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SetupWorkspaceCommand.RunAsync(
                Pool(),
                sessions,
                "https://example.invalid/store.git",
                [],
                "/irrelevant",
                NullLogger.Instance,
                default,
                store
            )
        );
    }

    [Fact]
    public async Task Root_cleanup_uncertainty_keeps_bootstrap_and_all_slots_quarantined()
    {
        using var db = new TempSqliteDatabase();
        using var store = new CodeReviewDaemon.Sample.Persistence.ReviewStore(db.ConnectionString);
        var pool = Pool();
        await Assert.ThrowsAsync<RemoteWorkspaceOutcomeUnknownException>(() =>
            SetupWorkspaceCommand.RunAsync(
                pool,
                Sessions(new SetupRunner { UnknownRootCleanup = true }),
                "https://example.invalid/store.git",
                [],
                "/irrelevant",
                NullLogger.Instance,
                default,
                store
            )
        );
        store.HasUnsettledWorkspaceBootstrap().Should().BeTrue();
        (await pool.TryLeaseAsync("Nova", default)).Should().BeNull();
        (await pool.TryLeaseAsync("Widgets", default)).Should().BeNull();
    }

    [Fact]
    public void Bootstrap_intent_survives_store_restart_and_only_exact_owner_can_clear_it()
    {
        using var db = new TempSqliteDatabase();
        string token;
        using (var first = new CodeReviewDaemon.Sample.Persistence.ReviewStore(db.ConnectionString))
            token = first.BeginWorkspaceBootstrap();
        using var restarted = new CodeReviewDaemon.Sample.Persistence.ReviewStore(db.ConnectionString);
        restarted.HasUnsettledWorkspaceBootstrap().Should().BeTrue();
        Assert.Throws<InvalidOperationException>(() => restarted.SettleWorkspaceBootstrap("different-token"));
        restarted.HasUnsettledWorkspaceBootstrap().Should().BeTrue();
        restarted.SettleWorkspaceBootstrap(token);
        restarted.HasUnsettledWorkspaceBootstrap().Should().BeFalse();
    }

    private static FakeReviewSessionProvisioner Sessions(SetupRunner runner)
    {
        var files = new FakeSandboxFileSystem();
        foreach (
            var name in new[]
            {
                "review_pool_admin.py",
                "review_common.py",
                "review_reset.py",
                "review_setup.py",
                "review_git_prepare.py",
            }
        )
            files.Files[$"/workspace/{ReviewSetupScriptRunner.ScriptsRelativePath}/{name}"] = "# installed by LLM";
        files.Files[$"/workspace/{ReviewSetupScriptRunner.RootSetupScript}"] = "# installed by LLM";
        return new(new ReviewRunSession("shared-session", runner, files));
    }

    private sealed class SetupRunner : ISandboxCommandRunner
    {
        public string? UnknownOperation { get; init; }
        public string? FailedOperation { get; init; }
        public bool HistoryComplete { get; init; } = true;
        public bool UnknownRootCleanup { get; init; }
        private readonly HashSet<string> _existingSlots = [];
        public List<SandboxCommand> Commands { get; } = [];

        public Task<SandboxCommandResult> RunAsync(SandboxCommand command, CancellationToken ct)
        {
            Commands.Add(command);
            var argv = command.Argv;
            if (UnknownOperation is not null && argv.Contains(UnknownOperation))
                throw new IOException("connection lost while remote command may still run");
            if (FailedOperation is not null && argv.Contains(FailedOperation))
                return Task.FromResult(new SandboxCommandResult(1, "", "secret-stderr"));
            if (UnknownRootCleanup && argv.Contains("apply"))
                return Task.FromResult(new SandboxCommandResult(75, "{\"outcome\":\"unknown\"}", ""));
            if (argv.Contains("inventory"))
                return Task.FromResult(
                    new SandboxCommandResult(
                        0,
                        "{\"schema\":1,\"repositories\":[{\"repository\":\"Nova\",\"path\":\"repos/Nova\"}]}",
                        ""
                    )
                );
            if (argv.Contains(ReviewSetupScriptRunner.SlotPresenceScript))
                return Task.FromResult(
                    new SandboxCommandResult(
                        0,
                        JsonSerializer.Serialize(new { absent = !_existingSlots.Contains(argv[^1]) }),
                        ""
                    )
                );
            if (argv.Contains("warm"))
                _existingSlots.Add($".worktrees/{argv[4]}-{argv[6]}");
            if (argv.Contains(ReviewSetupScriptRunner.EvidenceScript))
            {
                using var request = JsonDocument.Parse(argv[^1]);
                var repository = request.RootElement.GetProperty("repository").GetString();
                var slot = request.RootElement.GetProperty("slot").GetInt32();
                return Task.FromResult(
                    new SandboxCommandResult(
                        0,
                        JsonSerializer.Serialize(
                            new
                            {
                                repository,
                                slot,
                                sourcePath = $".worktrees/{repository}-{slot}/repos/{repository}",
                                sourceStore = $"source-stores/repos-{repository}.git",
                            }
                        ),
                        ""
                    )
                );
            }
            var output =
                argv.Contains(ReviewSlotPreparer.InspectRootScript)
                    ? "{\"hasGitDirectory\":true,\"pristine\":false,\"safeReservedPaths\":true}"
                : argv.Contains("--show-toplevel") ? "/workspace"
                : argv.Contains("get-url") ? "https://example.invalid/store.git"
                : argv.Contains(ReviewSetupScriptRunner.RootSetupScript)
                    ? JsonSerializer.Serialize(
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
                    )
                : argv.Contains("prime-history") ? JsonSerializer.Serialize(new { complete = HistoryComplete })
                : "";
            return Task.FromResult(new SandboxCommandResult(0, output, ""));
        }
    }
}
