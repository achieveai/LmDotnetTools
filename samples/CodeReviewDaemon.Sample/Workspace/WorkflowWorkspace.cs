using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AchieveAi.LmDotnetTools.Sandbox;
using CodeReviewDaemon.Sample.Agents;
using CodeReviewDaemon.Sample.Configuration;
using CodeReviewDaemon.Sample.Orchestration;
using CodeReviewDaemon.Sample.Persistence;
using CodeReviewDaemon.Sample.Persistence.Models;

namespace CodeReviewDaemon.Sample.Workspace;

/// <summary>The trusted preparation result, separate from the authored workflow's JSON contracts.</summary>
internal sealed record WorkflowWorkspacePreparation(
    ReviewSlot Slot,
    PreparedCheckout Checkout,
    PreparedReviewWorkspace Workspace,
    JsonObject Output,
    string AssignmentId,
    JsonObject Admission,
    IReadOnlyDictionary<string, string>? ContextDocumentHashes = null
);

/// <summary>A parent-owned resource assignment stored in the existing artifact table for scoped CLI calls.</summary>
internal sealed record WorkflowWorkspaceAssignment(
    ReviewSlot Slot,
    bool Active,
    string AssignmentId,
    string WorkflowInstanceId
);

internal sealed class WorkspaceCapacityUnavailableException()
    : InvalidOperationException("No review workspace is currently available.");

/// <summary>Separates parent-process slot lifetime from deterministic preparation performed by a scoped script process.</summary>
internal sealed class WorkflowWorkspace
{
    internal const string AssignmentArtifactKind = "workflow-workspace-assignment";
    internal const string PreparationArtifactKind = "workflow-workspace-prepared";
    internal const string CheckoutArtifactKind = "workflow-workspace-checkout";
    internal const string RemotePreparationArtifactKind = "workflow-workspace-remote-preparation";
    internal const string PreparationDiagnosticArtifactKind = "workflow-workspace-preparation-diagnostic";

    private enum PreparationStatus
    {
        Running,
        Settled,
        OutcomeUnknown,
    }

    // Preserve the durable artifact's existing fields; expose states rather than combining flags at call sites.
    private sealed record RemotePreparationState(
        string AssignmentId,
        bool Settled,
        int OwnerProcessId = 0,
        long OwnerStartTimeUtcTicks = 0,
        bool OutcomeUnknown = false
    )
    {
        [System.Text.Json.Serialization.JsonIgnore]
        public PreparationStatus Status =>
            OutcomeUnknown ? PreparationStatus.OutcomeUnknown
            : Settled ? PreparationStatus.Settled
            : PreparationStatus.Running;
    }

    private void RecordPreparation(ReviewRun run, RemotePreparationState intent, PreparationStatus status) =>
        Save(
            run,
            RemotePreparationArtifactKind,
            intent with
            {
                Settled = status == PreparationStatus.Settled,
                OutcomeUnknown = status == PreparationStatus.OutcomeUnknown,
            }
        );

    private static RemotePreparationState BeginPreparation(string assignmentId)
    {
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        return new(assignmentId, false, process.Id, process.StartTime.ToUniversalTime().Ticks);
    }

    private static bool HasLiveOwner(RemotePreparationState state)
    {
        if (state.OutcomeUnknown || state.OwnerProcessId <= 0 || state.OwnerStartTimeUtcTicks <= 0)
            return false;
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(state.OwnerProcessId);
            return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == state.OwnerStartTimeUtcTicks;
        }
        catch (Exception exception)
            when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private readonly ReviewStore _store;
    private readonly CodeReviewDaemonOptions _options;
    private readonly ReviewSlotWorkspace _slots;
    private readonly IReviewSessionProvisioner _sessions;
    private readonly Func<ReviewSlot, ReviewRun, CancellationToken, Task<PreparedReviewWorkspace>> _adoptWorkspace;
    private readonly Func<ReviewRun, JsonObject, ReviewRunSession, CancellationToken, Task<JsonObject>>? _contextReader;
    private readonly ConcurrentDictionary<long, WorkflowWorkspaceAssignment> _leases = new();

    public WorkflowWorkspace(
        ReviewStore store,
        CodeReviewDaemonOptions options,
        ReviewSlotWorkspace slots,
        IReviewSessionProvisioner sessions,
        Func<ReviewSlot, ReviewRun, CancellationToken, Task<PreparedReviewWorkspace>> adoptWorkspace,
        ILoggerFactory loggerFactory,
        Func<ReviewRun, JsonObject, ReviewRunSession, CancellationToken, Task<JsonObject>>? contextReader = null
    )
    {
        _store = store;
        _options = options;
        _slots = slots;
        _sessions = sessions;
        _adoptWorkspace = adoptWorkspace;
        _contextReader = contextReader;
    }

    /// <summary>Called only by the parent daemon; scripts consume the durable assignment without leasing again.</summary>
    public async Task<ReviewSlot> AcquireAsync(
        ReviewRun run,
        string workflowInstanceId,
        CancellationToken cancellationToken
    ) =>
        await AcquireCoreAsync(run, workflowInstanceId, wait: true, cancellationToken).ConfigureAwait(false)
        ?? throw new InvalidOperationException("A blocking workspace acquisition returned no slot.");

    /// <summary>Attempts a fresh or affinity workspace lease without blocking the poll loop.</summary>
    public Task<ReviewSlot?> TryAcquireAsync(
        ReviewRun run,
        string workflowInstanceId,
        CancellationToken cancellationToken
    ) => AcquireCoreAsync(run, workflowInstanceId, wait: false, cancellationToken);

    private async Task<ReviewSlot?> AcquireCoreAsync(
        ReviewRun run,
        string workflowInstanceId,
        bool wait,
        CancellationToken cancellationToken
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowInstanceId);
        if (HasUnsafeRemotePreparation())
            throw new InvalidOperationException("The shared review workspace has unsettled remote preparation.");
        if (_leases.TryGetValue(run.Id, out var existing))
        {
            if (string.Equals(existing.WorkflowInstanceId, workflowInstanceId, StringComparison.Ordinal))
            {
                return existing.Slot;
            }
            if (!wait)
            {
                return null;
            }
            throw new WorkspaceCapacityUnavailableException();
        }
        WorkflowWorkspaceAssignment? previous = null;
        if (_store.TryGetLatestArtifact(run.Id, AssignmentArtifactKind) is { } prior)
        {
            previous =
                JsonSerializer.Deserialize<WorkflowWorkspaceAssignment>(prior.Payload)
                ?? throw new InvalidDataException("Workflow workspace assignment is invalid.");
            if (previous.Active)
            {
                if (!wait)
                {
                    return null;
                }
                throw new WorkspaceCapacityUnavailableException();
            }
        }
        var repository =
            _store.GetRepo(run.RepoId) ?? throw new InvalidOperationException("Run repository is missing.");
        if (
            previous is not null
            && !string.Equals(previous.Slot.RepositoryName, repository.RepoName, StringComparison.OrdinalIgnoreCase)
        )
            throw new InvalidDataException("Persisted slot belongs to a different repository.");
        var slot = previous switch
        {
            null when wait => await _slots
                .Pool.LeaseAsync(repository.RepoName, cancellationToken)
                .ConfigureAwait(false),
            null => await _slots.Pool.TryLeaseAsync(repository.RepoName, cancellationToken).ConfigureAwait(false),
            not null when wait => await _slots
                .Pool.LeasePreferredAsync(previous.Slot, cancellationToken)
                .ConfigureAwait(false),
            _ => await _slots.Pool.TryLeasePreferredAsync(previous.Slot, cancellationToken).ConfigureAwait(false),
        };
        if (slot is null)
        {
            return null;
        }
        var assignment = new WorkflowWorkspaceAssignment(
            slot,
            Active: true,
            Guid.NewGuid().ToString("N"),
            workflowInstanceId
        );
        if (!_leases.TryAdd(run.Id, assignment))
        {
            await _slots.Pool.ReturnAsync(slot, CancellationToken.None).ConfigureAwait(false);
            var winner = _leases[run.Id];
            if (IsOwner(winner, workflowInstanceId))
            {
                return winner.Slot;
            }
            if (!wait)
            {
                return null;
            }
            throw new WorkspaceCapacityUnavailableException();
        }
        try
        {
            Save(run, AssignmentArtifactKind, assignment);
            return slot;
        }
        catch
        {
            _leases.TryRemove(run.Id, out _);
            await _slots.Pool.ReturnAsync(slot, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    internal async Task RestoreDiscussionReadAssignmentAsync(
        ReviewRun run,
        string instanceId,
        JsonObject admission,
        CancellationToken ct
    )
    {
        var assignment = Read<WorkflowWorkspaceAssignment>(run.Id, AssignmentArtifactKind);
        var prepared = Read<WorkflowWorkspacePreparation>(run.Id, PreparationArtifactKind);
        if (
            assignment.WorkflowInstanceId != instanceId
            || prepared.AssignmentId != assignment.AssignmentId
            || prepared.Slot != assignment.Slot
            || !JsonNode.DeepEquals(prepared.Admission, admission)
            || prepared.Checkout.SourceHeadSha != run.HeadSha
            || prepared.Checkout.TargetBaseSha != run.BaseSha
            || HasAnyUnsettledPreparation(_store)
            || _store
                .ListActiveWorkflowWorkspaceRuns()
                .Any(other =>
                    other.Id != run.Id
                    && Read<WorkflowWorkspaceAssignment>(other.Id, AssignmentArtifactKind).Slot == assignment.Slot
                )
        )
            throw new InvalidOperationException("Failed discussion recovery cannot prove original slot ownership.");
        var slot = assignment.Active
            ? await RecoverAsync(run, assignment.AssignmentId, instanceId, ct).ConfigureAwait(false)
            : await _slots.Pool.TryLeasePreferredAsync(assignment.Slot, ct).ConfigureAwait(false)
                ?? throw new WorkspaceCapacityUnavailableException();
        try
        {
            var session = await _sessions.GetOrCreateSharedAsync(ct).ConfigureAwait(false);
            await new ReviewSetupScriptRunner(session.CommandRunner, session.FileSystem, false)
                .VerifyPreparedAsync(slot, run with { MergeSha = prepared.Checkout.CheckoutSha }, ct)
                .ConfigureAwait(false);
            var restored = assignment with { Active = true };
            if (!assignment.Active)
            {
                if (!_leases.TryAdd(run.Id, restored))
                    throw new InvalidOperationException("Another lease already owns this review.");
                Save(run, AssignmentArtifactKind, restored);
            }
            if (await ReconcilePreparationAsync(run, admission, instanceId, ct).ConfigureAwait(false) is null)
                throw new InvalidOperationException("Original prepared context is unavailable.");
        }
        catch
        {
            _store.TryMarkReviewRunParked(
                run.Id,
                DateTimeOffset.UtcNow,
                "Discussion recovery could not verify its original workspace; ownership retained for reconciliation."
            );
            _leases.TryRemove(run.Id, out _);
            await _slots.Pool.RetireAsync(slot, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public ReviewSlot ReadAssignedSlot(ReviewRun run)
    {
        var assignment = ReadAssignment(run);
        return assignment.Slot;
    }

    /// <summary>Restores the original resource lease after restart; never prepares or cleans an unfinished workspace.</summary>
    public async Task<ReviewSlot> RecoverAsync(
        ReviewRun run,
        string assignmentId,
        string workflowInstanceId,
        CancellationToken cancellationToken
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowInstanceId);
        var assignment = ReadAssignment(run);
        if (assignment.AssignmentId != assignmentId)
        {
            throw new InvalidOperationException("Recovery does not match the persisted workspace assignment.");
        }
        if (string.IsNullOrWhiteSpace(assignment.WorkflowInstanceId))
        {
            throw new InvalidDataException("Workflow workspace assignment has no owner.");
        }
        if (!string.Equals(assignment.WorkflowInstanceId, workflowInstanceId, StringComparison.Ordinal))
        {
            throw new WorkspaceCapacityUnavailableException();
        }
        if (_leases.TryGetValue(run.Id, out var existing))
        {
            return
                existing.AssignmentId == assignmentId
                && string.Equals(existing.WorkflowInstanceId, workflowInstanceId, StringComparison.Ordinal)
                ? existing.Slot
                : throw new InvalidOperationException("Run already holds a different workspace assignment.");
        }
        var slot = await _slots.Pool.RecoverLeaseAsync(assignment.Slot, cancellationToken).ConfigureAwait(false);
        if (!_leases.TryAdd(run.Id, assignment))
        {
            await _slots.Pool.RetireAsync(slot, CancellationToken.None).ConfigureAwait(false);
            throw new InvalidOperationException("Run was concurrently recovered; the extra address has been retired.");
        }
        return slot;
    }

    public WorkflowWorkspaceAssignment ReadAssignment(ReviewRun run)
    {
        var assignment = Read<WorkflowWorkspaceAssignment>(run.Id, AssignmentArtifactKind);
        if (!assignment.Active || string.IsNullOrWhiteSpace(assignment.AssignmentId))
        {
            throw new InvalidOperationException("This run's workspace assignment is no longer active.");
        }
        return assignment;
    }

    public WorkflowWorkspacePreparation ReadPreparation(ReviewRun run)
    {
        var prepared = Read<WorkflowWorkspacePreparation>(run.Id, PreparationArtifactKind);
        var assignment = ReadAssignment(run);
        if (prepared.Slot != assignment.Slot || prepared.AssignmentId != assignment.AssignmentId)
        {
            throw new InvalidOperationException("Prepared workspace belongs to an obsolete slot assignment.");
        }
        return prepared;
    }

    /// <summary>Proves preparation belongs to the active frozen admission and its context artifact still exists.</summary>
    public async Task<JsonObject?> ReconcilePreparationAsync(
        ReviewRun run,
        JsonObject admission,
        string workflowInstanceId,
        CancellationToken cancellationToken
    )
    {
        WorkflowWorkspacePreparation prepared;
        try
        {
            prepared = ReadPreparation(run);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        if (!IsOwner(ReadAssignment(run), workflowInstanceId))
            return null;
        if (!JsonNode.DeepEquals(prepared.Admission, admission))
            return null;
        var name = $"workflow-context-{run.Id.ToString(CultureInfo.InvariantCulture)}-{prepared.AssignmentId}.json";
        var session = await _sessions.GetOrCreateSharedAsync(cancellationToken).ConfigureAwait(false);
        var context = await session
            .FileSystem.ReadFileAsync(
                $"{prepared.Checkout.NotesDir.TrimEnd('/', '\\')}/{name}",
                checked(_options.Limits.MaxArtifactPayloadChars * 4L),
                cancellationToken
            )
            .ConfigureAwait(false);
        if (context.Content is null)
            return null;
        if (
            prepared.Output["ContextArtifact"]?.GetValue<string>() is { } readablePath
            && readablePath.EndsWith(".md", StringComparison.Ordinal)
        )
        {
            var readable = await session
                .FileSystem.ReadFileAsync(
                    readablePath,
                    checked(_options.Limits.MaxArtifactPayloadChars * 4L),
                    cancellationToken
                )
                .ConfigureAwait(false);
            if (readable.Content is null)
                return null;
        }
        foreach (var document in prepared.ContextDocumentHashes ?? new Dictionary<string, string>())
        {
            // These are remote files; validate only the basename here, never probe the host filesystem.
            if (
                document.Key.Length == 0
                || document.Key is "." or ".."
                || document.Key.IndexOfAny(['/', '\\', ':']) >= 0
            )
                return null;
            var path = prepared.Checkout.NotesDir.TrimEnd('/', '\\') + "/" + document.Key;
            var content = await session
                .FileSystem.ReadFileAsync(
                    path,
                    checked(_options.Limits.MaxArtifactPayloadChars * 4L),
                    cancellationToken
                )
                .ConfigureAwait(false);
            if (
                content.Content is null
                || Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content.Content))) != document.Value
            )
                return null;
        }
        try
        {
            return JsonNode.Parse(context.Content) is JsonObject ? (JsonObject)prepared.Output.DeepClone() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Captures bounded, PR-scoped review output remotely, without resetting or traversing source files.</summary>
    public async Task<JsonArray> CaptureReviewNotesAsync(ReviewRun run, string workflowInstanceId, CancellationToken ct)
    {
        var prepared = Read<WorkflowWorkspacePreparation>(run.Id, PreparationArtifactKind);
        var assignment = Read<WorkflowWorkspaceAssignment>(run.Id, AssignmentArtifactKind);
        if (
            assignment.WorkflowInstanceId != workflowInstanceId
            || prepared.AssignmentId != assignment.AssignmentId
            || prepared.Slot != assignment.Slot
            || prepared.Admission["HeadSha"]?.GetValue<string>() != run.HeadSha
        )
            throw new InvalidOperationException("Review notes do not belong to this workflow assignment.");
        prepared.Slot.Validate();
        var session = await _sessions.GetOrCreateSharedAsync(ct).ConfigureAwait(false);
        var result = await session
            .CommandRunner.RunAsync(
                new Sandbox.SandboxCommand(
                    [
                        "python3",
                        "-c",
                        ReviewNotesCaptureScript,
                        prepared.Slot.WorktreeRelativePath,
                        run.PrId,
                        $"workflow-context-{run.Id.ToString(CultureInfo.InvariantCulture)}-{assignment.AssignmentId}.json",
                        run.HeadSha,
                        prepared.Slot.SourceRelativePath,
                        prepared.Checkout.CheckoutSha
                            ?? throw new InvalidOperationException("Prepared checkout SHA is missing."),
                        _options.Limits.MaxArtifactPayloadChars.ToString(CultureInfo.InvariantCulture),
                    ],
                    "/workspace"
                ),
                ct
            )
            .ConfigureAwait(false);
        if (!result.Succeeded || result.Stdout.Length > _options.Limits.MaxArtifactPayloadChars)
            throw new InvalidOperationException("Bounded remote review-note capture failed; preserve the slot.");
        var notes =
            JsonNode.Parse(result.Stdout) as JsonArray
            ?? throw new InvalidOperationException("Invalid remote review-note capture.");
        Save(run, "workflow-review-notes", notes);
        return notes;
    }

    internal const string ReviewNotesCaptureScript = """
        import hashlib, json, os, stat, subprocess, sys
        slot, pr, context_name, head, source, checkout, limit = sys.argv[1:]
        limit = int(limit)
        assert pr.isdecimal() and int(pr) > 0
        def read(relative, optional=False):
            parts = relative.split('/')
            assert all(p not in ('', '.', '..') for p in parts)
            fd = os.open('/workspace', os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
            try:
                for part in parts[:-1]:
                    next_fd = os.open(part, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW, dir_fd=fd)
                    os.close(fd)
                    fd = next_fd
                file_fd = os.open(parts[-1], os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK, dir_fd=fd)
                with os.fdopen(file_fd, 'rb') as handle:
                    info = os.fstat(handle.fileno())
                    assert stat.S_ISREG(info.st_mode) and info.st_size <= limit * 4
                    data = handle.read(limit * 4 + 1)
                    after = os.fstat(handle.fileno())
                    assert len(data) <= limit * 4
                    assert (info.st_size, info.st_mtime_ns, info.st_ctime_ns) == (after.st_size, after.st_mtime_ns, after.st_ctime_ns)
                    return data
            except FileNotFoundError:
                if optional:
                    return None
                raise
            finally:
                os.close(fd)
        source_head = subprocess.run(['git', '--no-optional-locks', '-C', '/workspace/' + source, 'rev-parse', 'HEAD'], check=True, capture_output=True, text=True, timeout=30).stdout.strip()
        assert source_head == checkout
        context = json.loads(read(slot + '/PRs/' + pr + '/' + context_name))
        assert context['Admission']['HeadSha'] == head and str(context['Admission']['PrId']) == pr
        def names(relative):
            fd = os.open('/workspace', os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
            try:
                for part in relative.split('/'):
                    assert part not in ('', '.', '..')
                    next_fd = os.open(part, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW, dir_fd=fd)
                    os.close(fd)
                    fd = next_fd
                entries = os.listdir(fd)
                assert len(entries) <= 4096
                return sorted(entries)
            except FileNotFoundError:
                return []
            finally:
                os.close(fd)
        import re
        candidates = []
        for directory in ('Memory/tasks', 'Memory/tasks/archive', 'PRs/' + pr, 'scripts'):
            for name in names(slot + '/' + directory):
                if directory.startswith('Memory/'):
                    selected = (name.startswith('pr-' + pr + '-') or name == 'review-pr-' + pr + '.md') and name.endswith('.md')
                elif directory == 'scripts':
                    selected = bool(re.search(r'(?<![0-9])' + pr + r'(?![0-9])', name)) and name.startswith(('repro-', 'reproduce-')) and name.endswith(('.ps1', '.py', '.sh'))
                else:
                    selected = name.endswith(('-report.md', '-assessment.md', '-evidence.json', '-repro.json')) and not name.startswith(('workflow-context-', 'verified-'))
                if selected:
                    candidates.append(directory + '/' + name)
                    assert len(candidates) <= 64
        notes = []
        total_bytes = 0
        for path in sorted(candidates):
            content = read(slot + '/' + path)
            total_bytes += len(content)
            assert total_bytes <= limit * 4
            notes.append({'RelativePath': path, 'Sha256': hashlib.sha256(content).hexdigest(), 'Content': content.decode('utf-8', errors='strict')})
        output = json.dumps(notes, ensure_ascii=True)
        assert len(output) <= limit
        print(output)
        """;

    /// <summary>Runs real preparation against the parent's assigned address and writes the provided context unchanged.</summary>
    public async Task<WorkflowWorkspacePreparation> PrepareAssignedAsync(
        ReviewRun run,
        JsonObject admission,
        string workflowInstanceId,
        CancellationToken cancellationToken
    )
    {
        if (_contextReader is null)
        {
            throw new InvalidOperationException(
                "A trusted frozen context reader is required for workflow preparation."
            );
        }
        if (
            admission["PrId"]?.GetValue<string>() != run.PrId
            || admission["HeadSha"]?.GetValue<string>() != run.HeadSha
        )
        {
            throw new InvalidOperationException("Prepared input does not match the trusted run identity.");
        }
        var assignment = ReadAssignment(run);
        if (!IsOwner(assignment, workflowInstanceId))
        {
            throw new WorkspaceCapacityUnavailableException();
        }
        var slot = assignment.Slot;
        var repo = _store.GetRepo(run.RepoId) ?? throw new InvalidOperationException("Run repository is missing.");
        var session = await _sessions.GetOrCreateSharedAsync(cancellationToken).ConfigureAwait(false);
        // Persist intent before dispatch: a killed CLI cannot falsely prove the remote operation stopped.
        if (HasUnsafeRemotePreparation())
            throw new InvalidOperationException("The shared review workspace has unsettled remote preparation.");
        if (HasUnsettledPreparation(run.Id))
            throw new InvalidOperationException("This slot already has unsettled remote preparation.");
        var intent = BeginPreparation(assignment.AssignmentId);
        RecordPreparation(run, intent, PreparationStatus.Running);
        var writeUnacknowledged = false;
        var phase = "checkout";
        string? writePath = null;
        string? contentSha256 = null;
        async Task WriteContextAsync(string path, string text)
        {
            phase = "context-write";
            writePath = path;
            contentSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
            writeUnacknowledged = true;
            RecordDiagnostic(null);
            await session.FileSystem.WriteFileAsync(path, text, cancellationToken).ConfigureAwait(false);
        }
        void RecordDiagnostic(Exception? error)
        {
            // Only typed metadata: exception messages and response bodies can contain credentials.
            var errors = new JsonArray();
            for (var current = error; current is not null && errors.Count < 8; current = current.InnerException)
            {
                var entry = new JsonObject { ["Type"] = current.GetType().Name };
                if (current is SandboxException sandbox)
                {
                    entry["Kind"] = sandbox.Kind.ToString();
                    entry["StatusCode"] = sandbox.StatusCode;
                    entry["ErrorCode"] = sandbox.ErrorCode switch
                    {
                        null => null,
                        "invalid_env"
                        or "session_not_found"
                        or "mount_not_found"
                        or "operation_not_found"
                        or "path_not_found"
                        or "idempotency_conflict"
                        or "operation_running"
                        or "target_locked"
                        or "workspace_required"
                        or "operation_api_unavailable"
                        or "operation_probe_failed"
                        or "operation_concurrency_limit"
                        or "operation_capacity_exhausted"
                        or "too_many_concurrent_requests"
                        or "sandbox_busy" => sandbox.ErrorCode,
                        _ => "unrecognized",
                    };
                    entry["OperationId"] =
                        sandbox.OperationId is { Length: > 0 and <= 128 } operationId
                        && operationId.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')
                            ? operationId
                            : null;
                }
                errors.Add(entry);
            }
            Save(
                run,
                PreparationDiagnosticArtifactKind,
                new JsonObject
                {
                    ["AssignmentId"] = assignment.AssignmentId,
                    ["SessionId"] = session.SessionId,
                    ["Phase"] = phase,
                    ["Path"] = writePath,
                    ["ContentSha256"] = contentSha256,
                    ["WriteUnacknowledged"] = writeUnacknowledged,
                    ["Errors"] = errors,
                }
            );
        }
        try
        {
            var checkout = await _slots
                .CreatePreparer(session)
                .PrepareAsync(run, slot, repo, cancellationToken)
                .ConfigureAwait(false);
            // The reader can resolve exact checkout evidence through this artifact without needing a task-specific C# input type.
            Save(run, CheckoutArtifactKind, checkout);
            phase = "context-read";
            var context = await _contextReader(run, admission, session, cancellationToken).ConfigureAwait(false);
            var name =
                $"workflow-context-{run.Id.ToString(CultureInfo.InvariantCulture)}-{assignment.AssignmentId}.json";
            var hostContextPath = $"{checkout.NotesDir.TrimEnd('/', '\\')}/{name}";
            var content = context.ToJsonString();
            cancellationToken.ThrowIfCancellationRequested();
            await WriteContextAsync(hostContextPath, content).ConfigureAwait(false);
            phase = "context-render";
            var readableName = Path.ChangeExtension(name, ".md");
            var readablePath = $"{checkout.NotesDir.TrimEnd('/', '\\')}/{readableName}";
            Dictionary<string, string>? documentHashes = null;
            if (context["ContextManifestVersion"]?.GetValue<int>() == 1)
            {
                documentHashes = [];
                foreach (var document in WorkflowMarkdown.ContextDocuments(context, readablePath))
                {
                    await WriteContextAsync(document.Key, document.Value).ConfigureAwait(false);
                    documentHashes.Add(
                        Path.GetFileName(document.Key),
                        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(document.Value)))
                    );
                }
            }
            else
            {
                await WriteContextAsync(
                        readablePath,
                        "# Prepared review context\n\n" + WorkflowMarkdown.YamlDocument(context)
                    )
                    .ConfigureAwait(false);
            }
            writeUnacknowledged = false;
            phase = "workspace-adopt";
            var workspace = await _adoptWorkspace(slot, run, cancellationToken).ConfigureAwait(false);
            var result = new WorkflowWorkspacePreparation(
                slot,
                checkout,
                workspace,
                new JsonObject
                {
                    ["PrId"] = run.PrId,
                    ["HeadSha"] = run.HeadSha,
                    ["WindowId"] = admission["WindowId"]?.DeepClone() ?? JsonValue.Create(string.Empty),
                    ["ContextArtifact"] = $"{checkout.NotesDir}/{readableName}",
                },
                assignment.AssignmentId,
                (JsonObject)admission.DeepClone(),
                documentHashes
            );
            if (ReadAssignment(run).AssignmentId != assignment.AssignmentId)
            {
                throw new InvalidOperationException("Workspace assignment changed during preparation.");
            }
            Save(run, PreparationArtifactKind, result);
            RecordPreparation(run, intent, PreparationStatus.Settled);
            return result;
        }
        catch (Exception exception) when (writeUnacknowledged || exception is RemoteWorkspaceOutcomeUnknownException)
        {
            RecordPreparation(run, intent, PreparationStatus.OutcomeUnknown);
            RecordDiagnostic(exception);
            throw exception is RemoteWorkspaceOutcomeUnknownException
                ? exception
                : new RemoteWorkspaceOutcomeUnknownException(exception);
        }
        catch (Exception exception)
        {
            RecordPreparation(run, intent, PreparationStatus.Settled);
            RecordDiagnostic(exception);
            throw;
        }
    }

    /// <summary>Invalidates the CLI assignment before returning a settled slot; unresolved activity retires its address.</summary>
    public async Task ReleaseOrRetireAsync(
        long runId,
        string workflowInstanceId,
        bool workSettled,
        CancellationToken cancellationToken
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowInstanceId);
        if (!_leases.TryGetValue(runId, out var current))
        {
            return;
        }
        if (!IsOwner(current, workflowInstanceId))
        {
            throw new InvalidOperationException(
                "Refusing to release another workflow instance's workspace assignment."
            );
        }
        if (!_leases.TryRemove(runId, out var assignment))
            return;
        try
        {
            var run = _store.GetReviewRun(runId) ?? throw new InvalidOperationException("Assigned run disappeared.");
            if (ReadAssignment(run).AssignmentId != assignment.AssignmentId)
            {
                throw new InvalidOperationException("Refusing to release a different workspace assignment.");
            }
            Save(run, AssignmentArtifactKind, assignment with { Active = false });
        }
        catch
        {
            // Failed invalidation cannot authorize reuse by another run while a CLI still resolves this assignment.
            await _slots.Pool.RetireAsync(assignment.Slot, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        workSettled &= !HasUnsettledPreparation(runId);
        await (
            workSettled
                ? _slots.Pool.ReturnAsync(assignment.Slot, CancellationToken.None)
                : _slots.Pool.RetireAsync(assignment.Slot, CancellationToken.None)
        ).ConfigureAwait(false);
    }

    internal void CompleteOperatorPreparationReset(ReviewRun run, string assignmentId)
    {
        var assignment = Read<WorkflowWorkspaceAssignment>(run.Id, AssignmentArtifactKind);
        var state = Read<RemotePreparationState>(run.Id, RemotePreparationArtifactKind);
        if (
            assignment.Active
            || assignment.AssignmentId != assignmentId
            || state.AssignmentId != assignmentId
            || _leases.ContainsKey(run.Id)
        )
            throw new InvalidOperationException("Operator reset no longer owns the failed preparation.");
        RecordPreparation(run, state, PreparationStatus.Settled);
    }

    private bool HasUnsettledPreparation(long runId) =>
        _store.TryGetLatestArtifact(runId, RemotePreparationArtifactKind) is { } artifact
        && JsonSerializer.Deserialize<RemotePreparationState>(artifact.Payload)?.Status != PreparationStatus.Settled;

    private bool HasUnsafeRemotePreparation() =>
        _store.HasUnsettledWorkspaceBootstrap()
        || _store
            .ListReviewRunsWithArtifact(RemotePreparationArtifactKind)
            .Any(run =>
            {
                var state = Read<RemotePreparationState>(run.Id, RemotePreparationArtifactKind);
                if (state.Status == PreparationStatus.Settled)
                    return false;
                var assignmentArtifact = _store.TryGetLatestArtifact(run.Id, AssignmentArtifactKind);
                var assignment = assignmentArtifact is null
                    ? null
                    : JsonSerializer.Deserialize<WorkflowWorkspaceAssignment>(assignmentArtifact.Payload);
                return !HasLiveOwner(state)
                    || assignment is not { Active: true }
                    || assignment.AssignmentId != state.AssignmentId;
            });

    internal static bool HasAnyUnsettledPreparation(ReviewStore store) =>
        store.HasUnsettledWorkspaceBootstrap()
        || store
            .ListReviewRunsWithArtifact(RemotePreparationArtifactKind)
            .Any(run =>
                JsonSerializer
                    .Deserialize<RemotePreparationState>(
                        store.TryGetLatestArtifact(run.Id, RemotePreparationArtifactKind)!.Payload
                    )
                    ?.Status != PreparationStatus.Settled
            );

    private static bool IsOwner(WorkflowWorkspaceAssignment assignment, string workflowInstanceId) =>
        string.Equals(assignment.WorkflowInstanceId, workflowInstanceId, StringComparison.Ordinal);

    /// <summary>Called by parent reconciliation only after the old address was quarantined or its owner proved stopped.</summary>
    public void InvalidateReconciledAssignment(ReviewRun run, string assignmentId)
    {
        var assignment = ReadAssignment(run);
        if (assignment.AssignmentId != assignmentId || _leases.ContainsKey(run.Id))
        {
            throw new InvalidOperationException("Reconciliation does not match an abandoned assignment.");
        }
        Save(run, AssignmentArtifactKind, assignment with { Active = false });
    }

    private T Read<T>(long runId, string kind) =>
        JsonSerializer.Deserialize<T>(
            _store.TryGetLatestArtifact(runId, kind)?.Payload
                ?? throw new InvalidOperationException($"Missing required '{kind}' artifact.")
        ) ?? throw new InvalidOperationException($"Invalid '{kind}' artifact.");

    private void Save<T>(ReviewRun run, string kind, T value) =>
        _store.AddArtifact(
            new ReviewArtifact
            {
                ReviewRunId = run.Id,
                ArtifactSchemaVersion = 1,
                ArtifactKind = kind,
                Provider = RepoIdentity.ToPublisherNamespace(_store.GetRepo(run.RepoId)!.Provider),
                Payload = JsonSerializer.Serialize(value),
            }
        );
}
