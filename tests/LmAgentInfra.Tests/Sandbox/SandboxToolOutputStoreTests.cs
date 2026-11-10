using System.Text;
using AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.Sandbox;
using Microsoft.Extensions.Logging.Abstractions;

namespace AchieveAi.LmDotnetTools.LmAgentInfra.Tests.Sandbox;

/// <summary>
/// The sandbox store writes a thread's tool output into that thread's workspace, in a form the
/// model's file tools can open and git will not pick up (#930 item 2).
/// </summary>
public sealed class SandboxToolOutputStoreTests
{
    private const string ThreadId = "thread-1";
    private const string WorkspaceId = "workspace-1";

    private static SandboxToolOutputStore CreateStore(FakeBrowser browser) =>
        new(browser, ThreadId, WorkspaceId, NullLogger.Instance);

    [Fact]
    public async Task Save_writes_the_gitignore_then_the_exact_text_and_returns_a_workspace_path()
    {
        var browser = new FakeBrowser();

        var path = await CreateStore(browser).SaveAsync("héllo ☃");

        browser.Writes.Should().HaveCount(2);
        var (ignoreSession, ignorePath, ignoreBytes) = browser.Writes[0];
        var (outputSession, outputPath, outputBytes) = browser.Writes[1];
        ignoreSession.Should().Be(FakeBrowser.SessionId);
        outputSession.Should().Be(FakeBrowser.SessionId);
        ignorePath.Should().MatchRegex(@"^\.tool-output/[0-9a-f]{32}/\.gitignore$");
        Encoding.UTF8.GetString(ignoreBytes).Should().Be("*\n");
        outputPath.Should().Be(ignorePath.Replace(".gitignore", "output.txt", StringComparison.Ordinal));
        Encoding.UTF8.GetString(outputBytes).Should().Be("héllo ☃");
        path.Should().Be("/workspace/" + outputPath);
        browser.ResolvedThread.Should().Be(ThreadId);
        browser.ResolvedWorkspace.Should().Be(WorkspaceId);
    }

    [Fact]
    public async Task Each_save_gets_its_own_folder()
    {
        var store = CreateStore(new FakeBrowser());

        var first = await store.SaveAsync("a");
        var second = await store.SaveAsync("a");

        first.Should().NotBe(second);
    }

    [Theory]
    [InlineData(SandboxSessionResolutionOutcome.NoSession)]
    [InlineData(SandboxSessionResolutionOutcome.CredentialConflict)]
    public async Task No_live_session_means_not_saved_and_nothing_written(SandboxSessionResolutionOutcome outcome)
    {
        var browser = new FakeBrowser { Outcome = outcome };

        var path = await CreateStore(browser).SaveAsync("a");

        path.Should().BeNull();
        browser.Writes.Should().BeEmpty();
    }

    [Fact]
    public async Task A_save_waiting_on_a_session_recreate_ends_with_its_token()
    {
        var browser = new FakeBrowser { HoldResolve = new TaskCompletionSource() };
        using var cts = new CancellationTokenSource();

        var saving = CreateStore(browser).SaveAsync("a", cts.Token);
        await cts.CancelAsync();

        var act = () => saving.WaitAsync(TimeSpan.FromSeconds(10));
        await act.Should().ThrowAsync<OperationCanceledException>();
        browser.Attempts.Should().BeEmpty();
    }

    [Fact]
    public async Task A_save_during_a_session_recreate_gives_a_not_saved_preview_within_its_time()
    {
        var browser = new FakeBrowser { HoldResolve = new TaskCompletionSource() };
        var options = new ToolOutputSpillOptions
        {
            Store = CreateStore(browser),
            MaxInlineBytes = 1024,
            SaveTimeout = TimeSpan.FromMilliseconds(20),
        };

        var result = await options
            .ApplyAsync(new ToolCallResult("c1", new string('x', 5_000)), NullLogger.Instance)
            .WaitAsync(TimeSpan.FromSeconds(10));

        Encoding.UTF8.GetByteCount(result.Result).Should().BeLessThanOrEqualTo(1024);
        result.Result.Should().Contain("could not be saved");
    }

    [Fact]
    public async Task The_output_is_not_written_when_its_gitignore_cannot_be()
    {
        var browser = new FakeBrowser { FailWrites = true };

        var act = () => CreateStore(browser).SaveAsync("a");

        await act.Should().ThrowAsync<IOException>();
        browser.Attempts.Should().ContainSingle().Which.Should().EndWith(".gitignore");
    }

    [Fact]
    public async Task A_failed_output_write_after_the_gitignore_is_not_saved_and_the_next_save_uses_a_new_folder()
    {
        var browser = new FakeBrowser { FailNextOutputWrite = true };
        var options = new ToolOutputSpillOptions { Store = CreateStore(browser), MaxInlineBytes = 1024 };
        var text = new string('x', 5_000);

        var failed = await options.ApplyAsync(new ToolCallResult("c1", text), NullLogger.Instance);
        var later = await options.ApplyAsync(new ToolCallResult("c2", text), NullLogger.Instance);

        Encoding.UTF8.GetByteCount(failed.Result).Should().BeLessThanOrEqualTo(1024);
        failed.Result.Should().Contain("could not be saved").And.NotContain(SandboxToolOutputStore.RootDirectory);
        later.Result.Should().Contain(SandboxToolOutputStore.RootDirectory);
        browser
            .Attempts.Where(path => path.EndsWith(SandboxToolOutputStore.OutputFileName, StringComparison.Ordinal))
            .Distinct()
            .Should()
            .HaveCount(2);
    }

    private sealed class FakeBrowser : IWorkspaceFileBrowser
    {
        public const string SessionId = "session-1";

        public SandboxSessionResolutionOutcome Outcome { get; init; } = SandboxSessionResolutionOutcome.Resolved;

        public bool FailWrites { get; init; }

        public bool FailNextOutputWrite { get; set; }

        public List<(string Session, string Path, byte[] Bytes)> Writes { get; } = [];

        public List<string> Attempts { get; } = [];

        public string? ResolvedThread { get; private set; }

        public string? ResolvedWorkspace { get; private set; }

        /// <summary>When set, resolving a session waits on this and ignores its token, as a recreate does.</summary>
        public TaskCompletionSource? HoldResolve { get; init; }

        public async Task<SandboxSessionResolution> ResolveThreadWorkspaceSessionForBackgroundAsync(
            string threadId,
            string persistedWorkspaceId,
            CancellationToken ct = default
        )
        {
            ResolvedThread = threadId;
            ResolvedWorkspace = persistedWorkspaceId;
            if (HoldResolve != null)
            {
                await HoldResolve.Task;
            }

            var session =
                Outcome == SandboxSessionResolutionOutcome.Resolved
                    ? new SandboxSession(persistedWorkspaceId, SessionId, "rel", "host")
                    : null;
            return new SandboxSessionResolution(Outcome, session, null, null);
        }

        public Task WriteWorkspaceFileBytesAsync(
            string sessionId,
            string relativePath,
            byte[] bytes,
            CancellationToken ct = default
        )
        {
            Attempts.Add(relativePath);
            if (FailWrites)
            {
                throw new IOException("write refused");
            }

            if (
                FailNextOutputWrite
                && relativePath.EndsWith(SandboxToolOutputStore.OutputFileName, StringComparison.Ordinal)
            )
            {
                FailNextOutputWrite = false;
                throw new IOException("write refused");
            }

            Writes.Add((sessionId, relativePath, bytes));
            return Task.CompletedTask;
        }

        public Task<SandboxSessionResolution> ResolveThreadWorkspaceSessionAsync(
            string threadId,
            string persistedWorkspaceId,
            SandboxCredential? requestCredential,
            CancellationToken ct = default
        ) => throw new NotSupportedException();

        public Task<IReadOnlyList<SandboxDirectoryEntry>> ListWorkspaceDirectoryAsync(
            string sessionId,
            string relativePath,
            CancellationToken ct = default
        ) => throw new NotSupportedException();

        public Task<byte[]> ReadWorkspaceFileBytesAsync(
            string sessionId,
            string relativePath,
            long? maxBytes,
            CancellationToken ct = default
        ) => throw new NotSupportedException();

        public Task<SandboxCommandResult> ExecuteWorkspaceCommandAsync(
            string sessionId,
            SandboxCommand command,
            CancellationToken ct = default
        ) => throw new NotSupportedException();
    }
}
