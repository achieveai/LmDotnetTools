using CodeReviewDaemon.Sample.Tests.Infrastructure;
using CodeReviewDaemon.Sample.Workspace.Git;

namespace CodeReviewDaemon.Sample.Tests.Scenarios;

/// <summary>
/// P3.3 — every git invocation runs untrusted PR/submodule code, so <see cref="GitRunner"/> must
/// prepend the hardening config (disable local/exec transports, neutralize hooks) to EVERY command.
/// These tests pin that no call site can reach git without the flags.
/// </summary>
public sealed class GitRunnerTests
{
    [Fact]
    public async Task RunAsync_prepends_the_hardening_flags_before_the_git_args()
    {
        var fake = new FakeSandboxCommandRunner();
        var runner = new GitRunner(fake);

        await runner.RunAsync(["status"], "/work/repo", CancellationToken.None);

        var argv = fake.Commands.Single().Argv;
        argv
            .Should()
            .Equal(
                "git",
                "-c",
                "protocol.file.allow=never",
                "-c",
                "protocol.ext.allow=never",
                "-c",
                "core.hooksPath=/dev/null",
                "-c",
                "user.name=Revobot",
                "-c",
                "user.email=revobot@revobot.local",
                "status");
    }

    [Fact]
    public async Task RunAsync_commits_under_the_configured_bot_name()
    {
        // CodeReviewDaemonOptions.BotName always documented itself as the retention commits' user.name,
        // but the identity was a hardcoded constant, so every commit in every store landed as
        // "AchieveAi Review Bot" however the operator had configured the daemon.
        var fake = new FakeSandboxCommandRunner();
        var runner = new GitRunner(fake, "Revobot (Nova)");

        await runner.RunAsync(["commit", "-m", "notes"], "/work/repo", CancellationToken.None);

        fake.Commands.Single().Argv.Should().ContainInOrder("-c", "user.name=Revobot (Nova)");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task RunAsync_falls_back_to_the_default_name_rather_than_committing_namelessly(string? botName)
    {
        var fake = new FakeSandboxCommandRunner();
        var runner = new GitRunner(fake, botName);

        await runner.RunAsync(["commit", "-m", "notes"], "/work/repo", CancellationToken.None);

        fake.Commands.Single().Argv.Should().ContainInOrder("-c", "user.name=Revobot");
    }

    [Fact]
    public async Task RunAsync_keeps_one_committer_address_whatever_the_bot_is_called()
    {
        // The address is the identity key the store's history is grouped by, so renaming the bot must not
        // split one bot's commits across two authors.
        var fake = new FakeSandboxCommandRunner();

        await new GitRunner(fake, "Revobot (Nova)").RunAsync(["status"], null, CancellationToken.None);
        await new GitRunner(fake, "GB's Revobot").RunAsync(["status"], null, CancellationToken.None);

        fake.Commands
            .Select(c => c.Argv.Single(a => a.StartsWith("user.email=", StringComparison.Ordinal)))
            .Should()
            .AllBe("user.email=revobot@revobot.local");
    }

    [Fact]
    public async Task RunAsync_passes_the_working_directory_through()
    {
        var fake = new FakeSandboxCommandRunner();
        var runner = new GitRunner(fake);

        await runner.RunAsync(["fetch", "origin"], "/work/repo", CancellationToken.None);

        fake.Commands.Single().WorkingDirectory.Should().Be("/work/repo");
    }

    [Fact]
    public async Task RunAsync_keeps_attacker_influenced_tokens_as_distinct_argv_entries()
    {
        var fake = new FakeSandboxCommandRunner();
        var runner = new GitRunner(fake);

        await runner.RunAsync(
            ["checkout", "--", "feature/$(rm -rf ~)"],
            "/work/repo",
            CancellationToken.None);

        // The dangerous token must remain a single, separate argv element (quoted at the boundary),
        // never concatenated into a command string here.
        fake.Commands.Single().Argv.Should().ContainSingle(a => a == "feature/$(rm -rf ~)");
    }

    [Fact]
    public async Task RunAsync_throws_on_empty_git_args()
    {
        var runner = new GitRunner(new FakeSandboxCommandRunner());

        var act = () => runner.RunAsync([], null, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
    }
}
