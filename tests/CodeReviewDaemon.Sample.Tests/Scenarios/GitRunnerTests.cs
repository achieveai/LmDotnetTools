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
                "user.email=review-bot@achieveai.local",
                "status");
    }

    [Fact]
    public async Task RunAsync_uses_the_configured_bot_name_as_the_git_identity()
    {
        var fake = new FakeSandboxCommandRunner();
        var runner = new GitRunner(fake, botName: "GB's Revobot");

        await runner.RunAsync(["status"], "/work/repo", CancellationToken.None);

        var argv = fake.Commands.Single().Argv;
        argv.Should().Contain("user.name=GB's Revobot");
        // The email stays the fixed, operator-independent constant regardless of the configured bot name.
        argv.Should().Contain("user.email=review-bot@achieveai.local");
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
