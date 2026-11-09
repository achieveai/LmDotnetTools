using System.Text;
using AchieveAi.LmDotnetTools.LmCore.Messages;

namespace AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox;

/// <summary>
/// Saves oversized tool output into a conversation's sandbox workspace, where the model's
/// <c>Read</c> and <c>Grep</c> tools can reach it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the workspace and not <c>/tmp</c>.</b> The gateway's file API only addresses mounts, and
/// its <c>Read</c> tool only allows the workspace and mounts, so a <c>/tmp</c> file could be neither
/// written here nor opened by the model.
/// </para>
/// <para>
/// <b>Every save gets a new random folder.</b> The model can write in the workspace, so it could
/// plant a symlink on a fixed path. A folder nobody could predict means a save can only create new
/// files, never replace one. The <c>.gitignore</c> lives inside that folder for the same reason, and
/// it is written first so the output is never there without it.
/// </para>
/// </remarks>
public sealed class SandboxToolOutputStore : IToolOutputStore
{
    /// <summary>The workspace folder that holds every saved output.</summary>
    public const string RootDirectory = ".tool-output";

    /// <summary>The name of the file holding the saved text, inside its random folder.</summary>
    public const string OutputFileName = "output.txt";

    // The gateway maps this prefix to the workspace on every backend, whatever the sandbox's home is.
    private const string ModelWorkspaceRoot = "/workspace";

    private static readonly byte[] s_ignoreEverything = "*\n"u8.ToArray();

    private readonly IWorkspaceFileBrowser _files;
    private readonly string _threadId;
    private readonly string _workspaceId;
    private readonly ILogger _logger;

    /// <param name="files">The registry's workspace surface.</param>
    /// <param name="threadId">The conversation that owns the sandbox session. For a sub-agent, its root.</param>
    /// <param name="workspaceId">The conversation's workspace id.</param>
    /// <param name="logger">Receives a line when no session can be found.</param>
    public SandboxToolOutputStore(IWorkspaceFileBrowser files, string threadId, string workspaceId, ILogger logger)
    {
        _files = files ?? throw new ArgumentNullException(nameof(files));
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);
        _threadId = threadId;
        _workspaceId = workspaceId ?? throw new ArgumentNullException(nameof(workspaceId));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<string?> SaveAsync(string content, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        var resolution = await _files
            .ResolveThreadWorkspaceSessionForBackgroundAsync(_threadId, _workspaceId, ct)
            .ConfigureAwait(false);
        if (resolution is not { Outcome: SandboxSessionResolutionOutcome.Resolved, Session: { } session })
        {
            _logger.LogWarning(
                "No live sandbox session to save tool output: ThreadId={ThreadId}, Outcome={Outcome}",
                _threadId,
                resolution.Outcome
            );
            return null;
        }

        var folder = $"{RootDirectory}/{Guid.NewGuid():N}";
        await _files
            .WriteWorkspaceFileBytesAsync(session.SessionId, $"{folder}/.gitignore", s_ignoreEverything, ct)
            .ConfigureAwait(false);

        var file = $"{folder}/{OutputFileName}";
        await _files
            .WriteWorkspaceFileBytesAsync(session.SessionId, file, Encoding.UTF8.GetBytes(content), ct)
            .ConfigureAwait(false);

        return $"{ModelWorkspaceRoot}/{file}";
    }
}
