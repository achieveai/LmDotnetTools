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

    /// <summary>
    /// When set, this double stops echoing <see cref="Resolution"/> and instead models the registry's
    /// REAL provenance rule: the app id the thread's <c>SandboxEstablishedBinding</c> was created
    /// under. A caller whose credential does not match it gets
    /// <see cref="SandboxSessionResolutionOutcome.CredentialConflict"/>, exactly as
    /// <c>SandboxSessionRegistry.ResolveThreadWorkspaceSessionAsync</c> does. Null (the default)
    /// leaves every existing test on the old echo behaviour.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This exists because the echo cannot express the defect in #253. Every mirror test resolved
    /// successfully no matter what credential the writer presented, so the writer's hard-coded
    /// <c>requestCredential: null</c> was invisible to the whole suite — an S2S-owned thread and a
    /// UI-owned one were literally the same fixture. A double that answers the same way for both
    /// cannot fail when the production code confuses them.
    /// </para>
    /// <para>
    /// Set it to a non-null app id to get an S2S-OWNED thread (the daemon created the conversation);
    /// leave it null for the interactive UI, whose bindings carry a null caller credential. That is
    /// the registry's own convention — provenance is compared raw and nullable, null meaning
    /// "interactive UI" — not an invention of this double.
    /// </para>
    /// </remarks>
    public string? OwnerAppId { get; set; }

    /// <summary>Credentials passed to <see cref="ResolveThreadWorkspaceSessionAsync"/>, in order.</summary>
    public List<SandboxCredential?> ResolveCredentials { get; } = [];

    /// <summary>The <c>persistedWorkspaceId</c> the caller passed to the last resolve call (the value
    /// <c>ReadWorkspaceId</c> extracted from metadata) — asserted by the JsonElement regression tests.</summary>
    public string? LastPersistedWorkspaceId { get; private set; }

    public Dictionary<string, IReadOnlyList<SandboxDirectoryEntry>> Listings { get; } = new(StringComparer.Ordinal);
    public byte[] FileBytes { get; set; } = [];
    public Exception? ReadThrows { get; set; }

    /// <summary>
    /// Makes every directory listing fail. A listing that fails is NOT the same as one that comes back
    /// empty — an empty <see cref="Listings"/> entry says "the directory holds nothing", whereas this says
    /// "the gateway could not tell you" — and the transcript writer's adoption path has to answer the two
    /// differently, so the double has to be able to express both.
    /// </summary>
    public Exception? ListThrows { get; set; }
    public Exception? WriteThrows { get; set; }

    /// <summary>
    /// Per-path write failure selector: returns the exception the write to that relative path should
    /// fail with, or null to let it through. <see cref="WriteThrows"/> fails EVERY path, which cannot
    /// express the case the containment tests need — the transcript itself is written and only
    /// <c>.conversations/.gitignore</c> fails.
    /// </summary>
    public Func<string, Exception?>? WriteFailure { get; set; }

    /// <summary>
    /// The default command outcome. <c>OperationRecordReleased</c> is true because that is what a healthy
    /// gateway does — the SDK deletes the operation record once it has read the result — so the default
    /// double does not look like the retained-record failure of issue #725. A test that wants that failure
    /// sets the flag false here (or through <see cref="ExecuteHandler"/>).
    /// </summary>
    public SandboxCommandResult ExecResult { get; set; } =
        new()
        {
            ExitCode = 0,
            StandardOutput = "",
            StandardError = "",
            OperationId = "op",
            OperationRecordReleased = true,
        };

    /// <summary>
    /// Per-command result selector. When set it wins over <see cref="ExecResult"/>; it may also throw, to
    /// exercise a caller's <see cref="SandboxException"/> handling.
    /// </summary>
    public Func<SandboxCommand, SandboxCommandResult>? ExecuteHandler { get; set; }

    public List<(string Path, byte[] Bytes)> Writes { get; } = [];
    public List<SandboxCommand> Commands { get; } = [];
    public int ReadCalls { get; private set; }

    public Task<SandboxSessionResolution> ResolveThreadWorkspaceSessionAsync(
        string threadId,
        string persistedWorkspaceId,
        SandboxCredential? requestCredential,
        CancellationToken ct = default
    )
    {
        LastPersistedWorkspaceId = persistedWorkspaceId;
        ResolveCredentials.Add(requestCredential);

        if (ResolveThrows is not null)
        {
            return Task.FromException<SandboxSessionResolution>(ResolveThrows);
        }

        if (OwnerAppId is not null && !string.Equals(OwnerAppId, requestCredential?.AppId, StringComparison.Ordinal))
        {
            return Task.FromResult(
                new SandboxSessionResolution(
                    SandboxSessionResolutionOutcome.CredentialConflict,
                    null,
                    OwnerAppId,
                    requestCredential?.AppId
                )
            );
        }

        return Task.FromResult(Resolution);
    }

    /// <summary>
    /// The background seam (#253): same resolution, no provenance comparison, because there is no
    /// caller. Modelled faithfully — <see cref="OwnerAppId"/> is deliberately NOT consulted here, so
    /// a test can tell the two methods apart. A double that answered both the same way would make
    /// the whole fix untestable.
    /// </summary>
    public Task<SandboxSessionResolution> ResolveThreadWorkspaceSessionForBackgroundAsync(
        string threadId,
        string persistedWorkspaceId,
        CancellationToken ct = default
    )
    {
        LastPersistedWorkspaceId = persistedWorkspaceId;
        BackgroundResolveCalls++;

        return ResolveThrows is not null
            ? Task.FromException<SandboxSessionResolution>(ResolveThrows)
            : Task.FromResult(Resolution);
    }

    /// <summary>How many times the background seam was used, so a test can assert the writer took it.</summary>
    public int BackgroundResolveCalls { get; private set; }

    public Task<IReadOnlyList<SandboxDirectoryEntry>> ListWorkspaceDirectoryAsync(
        string sessionId,
        string relativePath,
        CancellationToken ct = default
    )
    {
        if (ListThrows is not null)
        {
            return Task.FromException<IReadOnlyList<SandboxDirectoryEntry>>(ListThrows);
        }

        return Listings.TryGetValue(relativePath, out var entries)
            ? Task.FromResult(entries)
            : Task.FromResult<IReadOnlyList<SandboxDirectoryEntry>>([]);
    }

    public Task<byte[]> ReadWorkspaceFileBytesAsync(
        string sessionId,
        string relativePath,
        long? maxBytes,
        CancellationToken ct = default
    )
    {
        ReadCalls++;
        return ReadThrows is not null ? Task.FromException<byte[]>(ReadThrows) : Task.FromResult(FileBytes);
    }

    public Task WriteWorkspaceFileBytesAsync(
        string sessionId,
        string relativePath,
        byte[] bytes,
        CancellationToken ct = default
    )
    {
        if (WriteThrows is not null)
        {
            return Task.FromException(WriteThrows);
        }

        if (WriteFailure?.Invoke(relativePath) is { } failure)
        {
            return Task.FromException(failure);
        }

        Writes.Add((relativePath, bytes));
        return Task.CompletedTask;
    }

    public Task<SandboxCommandResult> ExecuteWorkspaceCommandAsync(
        string sessionId,
        SandboxCommand command,
        CancellationToken ct = default
    )
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
