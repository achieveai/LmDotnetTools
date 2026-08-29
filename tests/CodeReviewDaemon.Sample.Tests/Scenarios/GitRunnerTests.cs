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
        argv.Should()
            .Equal(
                "git",
                "-c",
                "protocol.file.allow=never",
                "-c",
                "protocol.ext.allow=never",
                "-c",
                "core.hooksPath=/dev/null",
                "-c",
                "core.longpaths=true",
                "-c",
                "user.name=AchieveAi Review Bot",
                "-c",
                "user.email=review-bot@achieveai.local",
                "status"
            );
    }

    [Fact]
    public async Task RunAsync_enables_long_paths_on_every_command()
    {
        // Without core.longpaths, Git for Windows refuses any path at or beyond MAX_PATH: the checkout fails
        // "Filename too long", `reset --hard` cannot repair it, and a fresh clone cannot check it out either —
        // so a pooled slot holding such a path is condemned and re-cloned on every lease, forever. It has to be
        // on EVERY invocation, not just hygiene's, because the clone and the review's own checkouts hit the
        // same wall. Pinned separately from the hardening flags so a reshuffle of that list cannot drop it.
        var fake = new FakeSandboxCommandRunner();
        var runner = new GitRunner(fake);

        await runner.RunAsync(["clone", "https://example.invalid/r.git", "store"], null, CancellationToken.None);

        fake.Commands.Single().Argv.Should().ContainSingle(a => a == "core.longpaths=true");
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

        await runner.RunAsync(["checkout", "--", "feature/$(rm -rf ~)"], "/work/repo", CancellationToken.None);

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
