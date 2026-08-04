using AchieveAi.LmDotnetTools.Sandbox;

namespace LmStreaming.Sample.Tests.TestDoubles;

/// <summary>
/// In-memory <see cref="IWorkspaceFileBrowser"/> stand-in for everything that talks to a sandbox
/// workspace without a gateway or a container: the file-browser HTTP routes (WI #195) and the workspace
/// transcript mirror (#251). It records every write and every command verbatim, so a test asserts the
/// exact bytes and the exact argv rather than a paraphrase of them.
/// </summary>
/// <remarks>
/// Shared rather than duplicated per suite: the two consumers must agree about the seam they are written
/// against, and a second copy is how they stop agreeing. <see cref="ExecuteHandler"/> is the one addition
/// the mirror needed — it issues several DIFFERENT commands in one flush (<c>tail</c>, <c>mv</c>, the
/// splice), and a single settable <see cref="ExecResult"/> cannot fail one of them while the others
/// succeed.
/// </remarks>
internal sealed class FakeFileBrowser : IWorkspaceFileBrowser
{
    /// <summary>The session a resolved outcome hands out.</summary>
    public static SandboxSession LiveSession => new("default", "sess-1", "/workspace", "/host/ws");

    public SandboxSessionResolution Resolution { get; set; } =
        new(SandboxSessionResolutionOutcome.Resolved, LiveSession, "app", null);

    public Exception? ResolveThrows { get; set; }

    /// <summary>The <c>persistedWorkspaceId</c> the caller passed to the last resolve call (the value
    /// <c>ReadWorkspaceId</c> extracted from metadata) — asserted by the JsonElement regression tests.</summary>
    public string? LastPersistedWorkspaceId { get; private set; }

    public Dictionary<string, IReadOnlyList<SandboxDirectoryEntry>> Listings { get; } = new(StringComparer.Ordinal);
    public byte[] FileBytes { get; set; } = [];
    public Exception? ReadThrows { get; set; }
    public Exception? WriteThrows { get; set; }
    public SandboxCommandResult ExecResult { get; set; } = new() { ExitCode = 0, StandardOutput = "", StandardError = "", OperationId = "op" };

    /// <summary>
    /// Per-command result selector. When set it wins over <see cref="ExecResult"/>; it may also throw, to
    /// exercise a caller's <see cref="SandboxException"/> handling.
    /// </summary>
    public Func<SandboxCommand, SandboxCommandResult>? ExecuteHandler { get; set; }

    public List<(string Path, byte[] Bytes)> Writes { get; } = [];
    public List<SandboxCommand> Commands { get; } = [];
    public int ReadCalls { get; private set; }

    public Task<SandboxSessionResolution> ResolveThreadWorkspaceSessionAsync(string threadId, string persistedWorkspaceId, SandboxCredential? requestCredential, CancellationToken ct = default)
    {
        LastPersistedWorkspaceId = persistedWorkspaceId;
        return ResolveThrows is not null ? Task.FromException<SandboxSessionResolution>(ResolveThrows) : Task.FromResult(Resolution);
    }

    public Task<IReadOnlyList<SandboxDirectoryEntry>> ListWorkspaceDirectoryAsync(string sessionId, string relativePath, CancellationToken ct = default) =>
        Listings.TryGetValue(relativePath, out var entries)
            ? Task.FromResult(entries)
            : Task.FromResult<IReadOnlyList<SandboxDirectoryEntry>>([]);

    public Task<byte[]> ReadWorkspaceFileBytesAsync(string sessionId, string relativePath, long? maxBytes, CancellationToken ct = default)
    {
        ReadCalls++;
        return ReadThrows is not null ? Task.FromException<byte[]>(ReadThrows) : Task.FromResult(FileBytes);
    }

    public Task WriteWorkspaceFileBytesAsync(string sessionId, string relativePath, byte[] bytes, CancellationToken ct = default)
    {
        if (WriteThrows is not null)
        {
            return Task.FromException(WriteThrows);
        }

        Writes.Add((relativePath, bytes));
        return Task.CompletedTask;
    }

    public Task<SandboxCommandResult> ExecuteWorkspaceCommandAsync(string sessionId, SandboxCommand command, CancellationToken ct = default)
    {
        Commands.Add(command);
        try
        {
            return Task.FromResult(ExecuteHandler is null ? ExecResult : ExecuteHandler(command));
        }
        catch (SandboxException ex)
        {
            return Task.FromException<SandboxCommandResult>(ex);
        }
    }
}
