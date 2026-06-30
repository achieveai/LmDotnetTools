using System.Text;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using CodeReviewDaemon.Sample.Workspace.Sandbox;

namespace CodeReviewDaemon.Sample.Tests.Scenarios;

/// <summary>
/// P3.2 — <see cref="SandboxFileSystem"/> moves file content across the single <c>Bash</c> tool
/// boundary base64-encoded, so arbitrary bytes survive and no content is interpolated into a shell
/// line. A read of a missing file is reported as <c>null</c> (the interface contract), not an error.
/// </summary>
public sealed class SandboxFileSystemTests
{
    [Fact]
    public async Task Write_sends_base64_content_through_a_decode_pipeline()
    {
        var runner = new FakeSandboxCommandRunner();
        var fs = new SandboxFileSystem(runner);
        const string content = "hello\nworld\n";

        await fs.WriteFileAsync("/work/reviewbot/README.md", content, CancellationToken.None);

        runner.Commands.Should().ContainSingle();
        var argv = runner.Commands[0].Argv;
        argv[0].Should().Be("sh");
        argv[1].Should().Be("-lc");

        var script = argv[2];
        var expectedPayload = Convert.ToBase64String(Encoding.UTF8.GetBytes(content));
        // Raw content never appears; only its base64 does, piped through `base64 -d` into the target.
        script.Should().Contain("mkdir -p '/work/reviewbot'");
        script.Should().Contain($"printf %s '{expectedPayload}' | base64 -d > '/work/reviewbot/README.md'");
    }

    [Fact]
    public async Task Read_decodes_base64_stdout_into_text()
    {
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes("knowledge"));
        var runner = new FakeSandboxCommandRunner();
        // GNU base64 wraps at 76 cols; emulate a trailing newline to prove whitespace is stripped.
        runner.OnArgvContains("base64 /work/reviewbot/KnowledgeBase/_toc.md", new SandboxCommandResult(0, encoded + "\n", string.Empty));
        var fs = new SandboxFileSystem(runner);

        var content = await fs.ReadFileAsync("/work/reviewbot/KnowledgeBase/_toc.md", CancellationToken.None);

        content.Should().Be("knowledge");
    }

    [Fact]
    public async Task Read_returns_null_when_the_file_is_missing()
    {
        var runner = new FakeSandboxCommandRunner();
        runner.OnArgvContains("base64", new SandboxCommandResult(1, string.Empty, "No such file"));
        var fs = new SandboxFileSystem(runner);

        var content = await fs.ReadFileAsync("/work/reviewbot/absent.md", CancellationToken.None);

        content.Should().BeNull();
    }

    [Fact]
    public async Task List_splits_ls_output_into_entry_names()
    {
        var runner = new FakeSandboxCommandRunner();
        runner.OnArgvContains("ls -1A '/work/reviewbot/KnowledgeBase'", new SandboxCommandResult(0, "_toc.md\n.gitkeep\nnull-checks.md\n", string.Empty));
        var fs = new SandboxFileSystem(runner);

        var names = await fs.ListFilesAsync("/work/reviewbot/KnowledgeBase", CancellationToken.None);

        names.Should().Equal("_toc.md", ".gitkeep", "null-checks.md");
    }

    [Fact]
    public async Task List_returns_empty_when_the_directory_is_missing()
    {
        var runner = new FakeSandboxCommandRunner();
        runner.OnArgvContains("ls -1A", new SandboxCommandResult(2, string.Empty, "No such file or directory"));
        var fs = new SandboxFileSystem(runner);

        var names = await fs.ListFilesAsync("/work/reviewbot/absent", CancellationToken.None);

        names.Should().BeEmpty();
    }
}
