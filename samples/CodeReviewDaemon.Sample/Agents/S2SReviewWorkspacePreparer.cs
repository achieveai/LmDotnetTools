using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Workspace;

namespace CodeReviewDaemon.Sample.Agents;

/// <summary>
/// Resolves the one LmStreaming catalog entry for the shared review workspace. A leased slot is only a
/// worktree path inside that workspace; it never becomes a catalog entry or Gateway mount of its own.
/// </summary>
internal sealed class S2SReviewWorkspacePreparer
{
    public const string DefaultWorkspaceLeaf = "nova-reviews";

    private readonly LmStreamingS2SClient _client;
    private readonly string? _reviewMarketplace;
    private readonly string _workspaceLeaf;
    private readonly ILogger<S2SReviewWorkspacePreparer> _logger;
    private readonly SemaphoreSlim _workspaceGate = new(1, 1);
    private string? _workspaceId;

    public S2SReviewWorkspacePreparer(
        LmStreamingS2SClient client,
        string? reviewMarketplace,
        ILogger<S2SReviewWorkspacePreparer> logger,
        string? workspaceLeaf = null
    )
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _reviewMarketplace = reviewMarketplace;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _workspaceLeaf = string.IsNullOrWhiteSpace(workspaceLeaf) ? DefaultWorkspaceLeaf : workspaceLeaf;
        if (!string.Equals(_workspaceLeaf, SanitizeLeaf(_workspaceLeaf), StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The shared workspace leaf is not stable under LmStreaming sanitization.",
                nameof(workspaceLeaf)
            );
        }
    }

    public async Task<PreparedReviewWorkspace> AdoptSlotAsync(
        ReviewSlot slot,
        ReviewRun run,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(slot);
        ArgumentNullException.ThrowIfNull(run);
        slot.Validate();

        var workspaceId = await EnsureSharedWorkspaceAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation(
            "Binding PR {PrId} to slot {SlotName} inside shared S2S workspace {WorkspaceId} ({WorkspaceLeaf}).",
            run.PrId,
            slot.Name,
            workspaceId,
            _workspaceLeaf
        );
        return new PreparedReviewWorkspace(
            _workspaceLeaf,
            workspaceId,
            run.PrId,
            slot.WorktreeRelativePath,
            slot.SourceRelativePath
        );
    }

    internal Task<string> EnsureSharedWorkspaceAsync(CancellationToken cancellationToken) =>
        EnsureWorkspaceForLeafAsync(_workspaceLeaf, "Nova reviews", cancellationToken);

    internal static string BuildWorktreeCwd(ReviewSlot slot)
    {
        ArgumentNullException.ThrowIfNull(slot);
        slot.Validate();
        return slot.WorktreeRelativePath;
    }

    internal static string SanitizeLeaf(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var lowered = raw.Trim().ToLowerInvariant();
        var collapsed = string.Join('-', lowered.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var invalid = new HashSet<char>(Path.GetInvalidFileNameChars()) { '/', '\\' };
        var sanitized = new string([.. collapsed.Where(character => !invalid.Contains(character))]);
        sanitized = sanitized.Replace("..", string.Empty, StringComparison.Ordinal);
        return sanitized.Trim('-');
    }

    internal async Task<string> EnsureWorkspaceForLeafAsync(
        string leaf,
        string name,
        CancellationToken cancellationToken
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(leaf);
        if (_workspaceId is not null)
        {
            return _workspaceId;
        }

        await _workspaceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_workspaceId is not null)
            {
                return _workspaceId;
            }

            var marketplaces = string.IsNullOrWhiteSpace(_reviewMarketplace)
                ? (IReadOnlyList<string>)[]
                : [_reviewMarketplace];
            var existing = await _client.ListWorkspacesAsync(cancellationToken).ConfigureAwait(false);
            foreach (var workspace in existing)
            {
                if (string.Equals(workspace.DirectoryRelPath, leaf, StringComparison.Ordinal))
                {
                    _workspaceId = workspace.Id;
                    _logger.LogInformation(
                        "Reusing shared S2S review workspace {WorkspaceId} for leaf {WorkspaceLeaf}.",
                        workspace.Id,
                        leaf
                    );
                    return _workspaceId;
                }
            }

            var created = await _client
                .CreateWorkspaceAsync(name, leaf, marketplaces, cancellationToken)
                .ConfigureAwait(false);
            _workspaceId = created.Id;
            _logger.LogInformation(
                "Created shared S2S review workspace {WorkspaceId} for leaf {WorkspaceLeaf}.",
                created.Id,
                leaf
            );
            return _workspaceId;
        }
        finally
        {
            _workspaceGate.Release();
        }
    }
}

internal sealed record PreparedReviewWorkspace(
    string Leaf,
    string WorkspaceId,
    string PrId,
    string WorkingDirectoryRelPath = "",
    string SourceRelativePath = ""
);
