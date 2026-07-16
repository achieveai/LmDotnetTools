using System.Text;
using AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox;
using CodeReviewDaemon.Sample.Configuration;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using CodeReviewDaemon.Sample.Workspace.Sandbox;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeReviewDaemon.Sample.Tests.Scenarios;

/// <summary>
/// <see cref="SandboxSessionAdapter"/> replaced the daemon's hand-rolled sandbox command/file protocol
/// (the deleted <c>SandboxOrchestrator</c> + <c>SandboxFileSystem</c> + <c>PosixShell</c>) with pure
/// mapping onto the typed Sandbox SDK's <c>SandboxClient</c> (#192). These tests drive the adapter
/// through a real <c>SandboxClient</c> over a <see cref="ScriptedSandboxGateway"/> that speaks the SDK's
/// public wire protocol, and pin every observable contract the daemon depends on: the real captured exit
/// code and stdout/stderr, the PR #121 H4 output cap, the per-command timeout surfacing as a
/// <see cref="TimeoutException"/>, read-missing → <c>null</c>, write-failure → <see cref="IOException"/>,
/// and list-missing → empty (dotfiles and NUL-embedded names preserved).
/// </summary>
public sealed class SandboxSessionAdapterTests
{
    private const string Gateway = "http://127.0.0.1:8080";
    private const string Session = "sess-1";

    private static SandboxSessionAdapter CreateAdapter(ScriptedSandboxGateway gateway, SandboxLimits? limits = null) =>
        new(
            Gateway,
            Session,
            NullLogger<SandboxSessionAdapter>.Instance,
            new SandboxCredential("codereview-daemon", string.Empty),
            limits,
            gateway
        );

    [Fact]
    public async Task RunAsync_returns_the_real_captured_exit_code_and_streams()
    {
        var gateway = new ScriptedSandboxGateway
        {
            CommandExitCode = 3,
            CommandStdout = "on stdout\n",
            CommandStderr = "on stderr\n",
        };
        await using var adapter = CreateAdapter(gateway);

        var result = await adapter.RunAsync(new SandboxCommand(["git", "status"]), CancellationToken.None);

        result.ExitCode.Should().Be(3);
        result.Succeeded.Should().BeFalse();
        result.Stdout.Should().Be("on stdout\n");
        // Unlike the old sentinel runner (which could never separate stderr and always returned it empty),
        // the SDK captures a genuine stderr and the adapter surfaces it.
        result.Stderr.Should().Be("on stderr\n");
    }

    [Fact]
    public async Task RunAsync_passes_an_absolute_working_directory_and_maps_the_result()
    {
        var gateway = new ScriptedSandboxGateway { CommandExitCode = 0, CommandStdout = "ok" };
        await using var adapter = CreateAdapter(gateway);

        // A /workspace-rooted working directory is accepted (stripped to the SDK's workspace-relative form).
        var result = await adapter.RunAsync(
            new SandboxCommand(["git", "rev-parse", "HEAD"], "/workspace/target"),
            CancellationToken.None
        );

        result.Succeeded.Should().BeTrue();
        result.Stdout.Should().Be("ok");
    }

    [Fact]
    public async Task RunAsync_caps_output_at_the_configured_limit()
    {
        var stdout = new string('x', 5000);
        var gateway = new ScriptedSandboxGateway { CommandExitCode = 0, CommandStdout = stdout };
        var limits = new SandboxLimits { MaxOutputChars = 1000 };
        await using var adapter = CreateAdapter(gateway, limits);

        var result = await adapter.RunAsync(new SandboxCommand(["cat", "big.txt"]), CancellationToken.None);

        result.Stdout.Should().Be(new string('x', 1000) + SandboxLimits.TruncationMarker);
    }

    [Fact]
    public async Task RunAsync_maps_a_gateway_execution_timeout_to_TimeoutException()
    {
        var gateway = new ScriptedSandboxGateway { SimulateExecutionTimeout = true };
        await using var adapter = CreateAdapter(gateway);

        var act = () => adapter.RunAsync(new SandboxCommand(["sleep", "600"]), CancellationToken.None);

        (await act.Should().ThrowAsync<TimeoutException>()).Which.Message.Should().Contain("timeout");
    }

    [Fact]
    public async Task ReadFileAsync_returns_the_exact_file_content()
    {
        var gateway = new ScriptedSandboxGateway { ReadBytes = Encoding.UTF8.GetBytes("knowledge\n") };
        await using var adapter = CreateAdapter(gateway);

        var content = await adapter.ReadFileAsync("/workspace/reviewbot/_toc.md", CancellationToken.None);

        content.Should().Be("knowledge\n");
    }

    [Fact]
    public async Task ReadFileAsync_reads_an_empty_file_as_empty_string()
    {
        var gateway = new ScriptedSandboxGateway { ReadBytes = [] };
        await using var adapter = CreateAdapter(gateway);

        var content = await adapter.ReadFileAsync("/workspace/reviewbot/empty.md", CancellationToken.None);

        content.Should().Be(string.Empty);
    }

    [Fact]
    public async Task ReadFileAsync_returns_null_when_the_file_is_missing()
    {
        var gateway = new ScriptedSandboxGateway { ReadMissing = true };
        await using var adapter = CreateAdapter(gateway);

        var content = await adapter.ReadFileAsync("/workspace/reviewbot/absent.md", CancellationToken.None);

        content.Should().BeNull();
    }

    [Fact]
    public async Task ReadFileAsync_rethrows_when_the_session_is_evicted()
    {
        // An evicted session collapses to SandboxErrorKind.NotFound just like a missing path, but its
        // error_code is session_not_found — the adapter must surface it as an error, NOT silently degrade
        // it to a missing file (null). Only a genuinely missing PATH degrades.
        var gateway = new ScriptedSandboxGateway { SessionEvicted = true };
        await using var adapter = CreateAdapter(gateway);

        var act = () => adapter.ReadFileAsync("/workspace/reviewbot/_toc.md", CancellationToken.None);

        var exception = await act.Should().ThrowAsync<AchieveAi.LmDotnetTools.Sandbox.SandboxException>();
        exception.Which.ErrorCode.Should().Be("session_not_found");
    }

    [Fact]
    public async Task ReadFileAsync_rethrows_on_a_bare_404_without_error_code()
    {
        // A code-less 404 is AMBIGUOUS — the direct API also 404s an evicted session — so it is NOT a
        // definitive missing path. The adapter must surface it as a real error, not degrade to null.
        var gateway = new ScriptedSandboxGateway { BareNotFound = true };
        await using var adapter = CreateAdapter(gateway);

        var act = () => adapter.ReadFileAsync("/workspace/reviewbot/legacy.md", CancellationToken.None);

        var exception = await act.Should().ThrowAsync<AchieveAi.LmDotnetTools.Sandbox.SandboxException>();
        exception.Which.Kind.Should().Be(AchieveAi.LmDotnetTools.Sandbox.SandboxErrorKind.NotFound);
        exception.Which.ErrorCode.Should().BeNull();
    }

    [Fact]
    public async Task ListFilesAsync_returns_entry_names_including_dotfiles_and_embedded_spaces()
    {
        var entries = new[] { "_toc.md", ".gitkeep", "null checks.md" };
        var gateway = new ScriptedSandboxGateway { ReadBytes = Encoding.UTF8.GetBytes(string.Join('\0', entries)) };
        await using var adapter = CreateAdapter(gateway);

        var names = await adapter.ListFilesAsync("/workspace/reviewbot/KnowledgeBase", CancellationToken.None);

        names.Should().Equal("_toc.md", ".gitkeep", "null checks.md");
    }

    [Fact]
    public async Task ListFilesAsync_returns_empty_when_the_directory_is_missing()
    {
        var gateway = new ScriptedSandboxGateway { ListMissing = true };
        await using var adapter = CreateAdapter(gateway);

        var names = await adapter.ListFilesAsync("/workspace/reviewbot/absent", CancellationToken.None);

        names.Should().BeEmpty();
    }

    [Fact]
    public async Task ListFilesAsync_rethrows_when_the_session_is_evicted()
    {
        // As with a read, an evicted session must surface as an error rather than a fake empty listing:
        // only a genuinely missing directory (path_not_found) degrades to empty.
        var gateway = new ScriptedSandboxGateway { SessionEvicted = true };
        await using var adapter = CreateAdapter(gateway);

        var act = () => adapter.ListFilesAsync("/workspace/reviewbot/KnowledgeBase", CancellationToken.None);

        var exception = await act.Should().ThrowAsync<AchieveAi.LmDotnetTools.Sandbox.SandboxException>();
        exception.Which.ErrorCode.Should().Be("session_not_found");
    }

    [Fact]
    public async Task ListFilesAsync_rethrows_on_a_bare_404_without_error_code()
    {
        // A code-less 404 is ambiguous (the direct API also 404s an evicted session), so it is NOT a
        // definitive missing path; the adapter must surface it as an error rather than a fake empty listing.
        var gateway = new ScriptedSandboxGateway { BareNotFound = true };
        await using var adapter = CreateAdapter(gateway);

        var act = () => adapter.ListFilesAsync("/workspace/reviewbot/legacy", CancellationToken.None);

        var exception = await act.Should().ThrowAsync<AchieveAi.LmDotnetTools.Sandbox.SandboxException>();
        exception.Which.Kind.Should().Be(AchieveAi.LmDotnetTools.Sandbox.SandboxErrorKind.NotFound);
        exception.Which.ErrorCode.Should().BeNull();
    }

    [Fact]
    public async Task WriteFileAsync_completes_when_the_write_finalizes()
    {
        var gateway = new ScriptedSandboxGateway();
        await using var adapter = CreateAdapter(gateway);

        var act = () => adapter.WriteFileAsync("/workspace/reviewbot/README.md", "hello\nworld\n", CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task WriteFileAsync_maps_a_write_failure_to_IOException()
    {
        var gateway = new ScriptedSandboxGateway { WriteFailsIntegrity = true };
        await using var adapter = CreateAdapter(gateway);

        var act = () => adapter.WriteFileAsync("/workspace/reviewbot/README.md", "content", CancellationToken.None);

        (await act.Should().ThrowAsync<IOException>())
            .Which.Message.Should()
            .Contain("/workspace/reviewbot/README.md");
    }
}
