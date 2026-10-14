using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AchieveAi.LmDotnetTools.LmCore.Utils;

namespace AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;

/// <summary>
/// File-based implementation of IConversationStore.
/// Stores messages and metadata as JSON files in a directory structure.
/// </summary>
public sealed class FileConversationStore
    : IConversationStore,
        IConversationOwnershipStore,
        IRunLedgerStore,
        IRunLifecycleStore,
        IInputAcceptanceStore
{
    private const string MessagesFileName = "messages.json";
    private const string MetadataFileName = "metadata.json";
    private const string RunsFileName = "runs.json";
    private const string AcceptedInputsFileName = "accepted-inputs.json";
    private const string AcceptancesDirectoryName = "acceptances";
    private const string MutationGateSuffix = ".mutate";

    /// <summary>
    /// How long a caller waits for an admission record that exists but is not yet readable before calling it
    /// a fault. The threshold separates "a live writer has not finished" from "a dead host left a half-written
    /// record", and any fixed threshold there is a starvation-class discriminator: the waiter is starved by the
    /// very load it is waiting on. New reservations publish a complete staged record atomically; the
    /// budget still protects readers of older or externally damaged records.
    /// </summary>
    private static readonly TimeSpan AcceptanceSettleTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan AcceptanceSettlePoll = TimeSpan.FromMilliseconds(5);

    // Deliberately a separate file from runs.json: the run ledger's shape is part of the status
    // API's contract, and lifecycle observation must be addable without rewriting it.
    private const string RunLifecycleFileName = "run-lifecycle.json";

    private readonly string _baseDirectory;
    private readonly TimeProvider _time;
    private readonly bool _strictMetadataReads;

    // Retain gates for the store lifetime, including after deletion: removing one while callers
    // still hold or await it would let a replacement gate protect the same directory concurrently.
    // This costs one entry per directory encountered, including reads of missing threads.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _directoryGates = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal
    );

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static readonly JsonSerializerOptions AcceptanceJsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// Creates a new FileConversationStore.
    /// </summary>
    /// <param name="baseDirectory">Base directory for storing conversation data.</param>
    /// <param name="timeProvider">
    /// Clock the admission-record settle budget is measured on. Injected so a test can prove what happens
    /// when the budget is genuinely spent without spending it in real time; production leaves it null.
    /// </param>
    public FileConversationStore(string baseDirectory, TimeProvider? timeProvider = null)
        : this(baseDirectory, timeProvider, strictMetadataReads: false) { }

    /// <summary>Creates a store that can fail closed on corrupt metadata instead of treating it as absent.</summary>
    public FileConversationStore(string baseDirectory, TimeProvider? timeProvider, bool strictMetadataReads)
    {
        _baseDirectory = baseDirectory ?? throw new ArgumentNullException(nameof(baseDirectory));
        _time = timeProvider ?? TimeProvider.System;
        _strictMetadataReads = strictMetadataReads;
        _ = Directory.CreateDirectory(_baseDirectory);
    }

    /// <inheritdoc />
    public async Task AppendMessagesAsync(
        string threadId,
        IReadOnlyList<PersistedMessage> messages,
        CancellationToken ct = default
    )
    {
        ArgumentNullException.ThrowIfNull(threadId);
        ArgumentNullException.ThrowIfNull(messages);

        if (messages.Count == 0)
        {
            return;
        }

        // Read before taking the lock: ForkHistory takes the per-thread gate itself.
        var forkPoint = await ForkHistory.ForkPointAsync(this, threadId, ct);

        var gate = GetDirectoryGate(GetThreadDirectory(threadId));
        await gate.WaitAsync(ct);
        try
        {
            var threadDir = GetThreadDirectory(threadId);
            _ = Directory.CreateDirectory(threadDir);

            var messagesFile = Path.Combine(threadDir, MessagesFileName);
            var existingMessages = await LoadMessagesFromFileAsync(messagesFile, ct);

            // The store owns Seq. A file written before the column existed has rows without one;
            // the first append numbers them in load order and writes them back, so the thread reads
            // the same before and after and the watermark becomes meaningful from here on.
            var allMessages = MessageSequence.Append(existingMessages, messages, forkPoint);
            await WriteJsonFileAsync(messagesFile, allMessages, ct);
        }
        finally
        {
            _ = gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<long> GetMessageWatermarkAsync(string threadId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(threadId);

        long own;
        var gate = GetDirectoryGate(GetThreadDirectory(threadId));
        await gate.WaitAsync(ct);
        try
        {
            var messagesFile = Path.Combine(GetThreadDirectory(threadId), MessagesFileName);
            var messages = await LoadMessagesFromFileAsync(messagesFile, ct);
            own = messages.Count == 0 ? 0 : MessageSequence.Watermark(messages);
        }
        finally
        {
            _ = gate.Release();
        }

        return await ForkHistory.WatermarkAsync(this, threadId, own, ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PersistedMessage>> LoadMessageRangeAsync(
        string threadId,
        long fromSeq,
        long toSeq,
        int limit,
        CancellationToken ct = default
    )
    {
        ArgumentNullException.ThrowIfNull(threadId);

        return await ForkHistory.RangeAsync(
            this,
            threadId,
            fromSeq,
            toSeq,
            limit,
            async (from, to, max) =>
            {
                var gate = GetDirectoryGate(GetThreadDirectory(threadId));
                await gate.WaitAsync(ct);
                try
                {
                    var messagesFile = Path.Combine(GetThreadDirectory(threadId), MessagesFileName);
                    var messages = await LoadMessagesFromFileAsync(messagesFile, ct);
                    return MessageSequence.Range(messages, from, to, max);
                }
                finally
                {
                    _ = gate.Release();
                }
            },
            ct
        );
    }

    /// <inheritdoc />
    public async Task NumberLegacyMessagesAsync(string threadId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(threadId);

        var gate = GetDirectoryGate(GetThreadDirectory(threadId));
        await gate.WaitAsync(ct);
        try
        {
            var messagesFile = Path.Combine(GetThreadDirectory(threadId), MessagesFileName);
            var existingMessages = await LoadMessagesFromFileAsync(messagesFile, ct);
            if (existingMessages.All(m => m.Seq is not null))
            {
                return;
            }

            await WriteJsonFileAsync(messagesFile, MessageSequence.Append(existingMessages, []), ct);
        }
        finally
        {
            _ = gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task ReplaceMessageAsync(string threadId, PersistedMessage replacement, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(threadId);
        ArgumentNullException.ThrowIfNull(replacement);

        var gate = GetDirectoryGate(GetThreadDirectory(threadId));
        await gate.WaitAsync(ct);
        try
        {
            var messagesFile = Path.Combine(GetThreadDirectory(threadId), MessagesFileName);
            var existing = await LoadMessagesFromFileAsync(messagesFile, ct);
            var idx = existing.FindIndex(m => m.Id == replacement.Id);
            if (idx < 0)
            {
                throw new InvalidOperationException($"Message '{replacement.Id}' not found in thread '{threadId}'.");
            }

            // Preserve the original timestamp and Seq so load ordering remains stable across
            // replacement: a replacement is a mutation in place, not an append, and must not move
            // the watermark.
            existing[idx] = replacement with
            {
                Timestamp = existing[idx].Timestamp,
                Seq = existing[idx].Seq,
                ParentMessageId = existing[idx].ParentMessageId,
            };
            await WriteJsonFileAsync(messagesFile, existing, ct);
        }
        finally
        {
            _ = gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PersistedMessage>> LoadMessagesAsync(
        string threadId,
        CancellationToken ct = default
    )
    {
        ArgumentNullException.ThrowIfNull(threadId);

        List<PersistedMessage> own;
        var gate = GetDirectoryGate(GetThreadDirectory(threadId));
        await gate.WaitAsync(ct);
        try
        {
            var messagesFile = Path.Combine(GetThreadDirectory(threadId), MessagesFileName);
            own = MessageSequence.Order(await LoadMessagesFromFileAsync(messagesFile, ct));
        }
        finally
        {
            _ = gate.Release();
        }

        return await ForkHistory.LoadAsync(this, threadId, own, ct);
    }

    /// <inheritdoc />
    public async Task SaveMetadataAsync(string threadId, ThreadMetadata metadata, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(threadId);

        var gate = GetDirectoryGate(GetThreadDirectory(threadId));
        await gate.WaitAsync(ct);
        try
        {
            var threadDir = GetThreadDirectory(threadId);
            _ = Directory.CreateDirectory(threadDir);

            var metadataFile = Path.Combine(threadDir, MetadataFileName);
            await WriteJsonFileAsync(metadataFile, metadata, ct);
        }
        finally
        {
            _ = gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<ThreadMetadata?> LoadMetadataAsync(string threadId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(threadId);

        var gate = GetDirectoryGate(GetThreadDirectory(threadId));
        await gate.WaitAsync(ct);
        try
        {
            var metadataFile = Path.Combine(GetThreadDirectory(threadId), MetadataFileName);
            return await LoadMetadataFileAsync(metadataFile, ct);
        }
        finally
        {
            _ = gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task UpdateMetadataAsync(
        string threadId,
        Func<ThreadMetadata?, ThreadMetadata> update,
        CancellationToken ct = default
    )
    {
        ArgumentNullException.ThrowIfNull(threadId);
        ArgumentNullException.ThrowIfNull(update);

        // Hold the lock across the read AND the write so a concurrent read-modify-write for the same
        // thread cannot interleave and clobber the other's properties (the provider-vs-workspace and
        // bindings-vs-title/preview lost-update race that dropped the persisted provider).
        var gate = GetDirectoryGate(GetThreadDirectory(threadId));
        await gate.WaitAsync(ct);
        try
        {
            var threadDir = GetThreadDirectory(threadId);
            _ = Directory.CreateDirectory(threadDir);

            var metadataFile = Path.Combine(threadDir, MetadataFileName);
            var existing = await LoadMetadataFileAsync(metadataFile, ct);
            var updated = update(existing);
            await WriteJsonFileAsync(metadataFile, updated, ct);
        }
        finally
        {
            _ = gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task DeleteThreadAsync(string threadId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(threadId);

        var gate = GetDirectoryGate(GetThreadDirectory(threadId));
        await gate.WaitAsync(ct);
        try
        {
            var threadDir = GetThreadDirectory(threadId);
            if (Directory.Exists(threadDir))
            {
                Directory.Delete(threadDir, recursive: true);
            }
        }
        finally
        {
            _ = gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ThreadMetadata>> ListThreadsAsync(
        int limit = 50,
        int offset = 0,
        ConversationListOptions? options = null,
        CancellationToken ct = default
    )
    {
        var metadataList = await ReadAllMetadataAsync(ct).ConfigureAwait(false);

        // Exclusion and ordering both run BEFORE Skip/Take. Applying either afterwards would
        // return a page short by however many rows it removed - the exact defect that hid real
        // conversations behind a wall of agent-owned threads. See ConversationListOptions.
        var listOptions = options ?? ConversationListOptions.Default;

        return [.. listOptions.Order(metadataList.Where(listOptions.Admits)).Skip(offset).Take(limit)];
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ThreadMetadata>> ListThreadsAsync(
        ConversationListScope scope,
        int limit = 50,
        int offset = 0,
        ConversationListOptions? options = null,
        CancellationToken ct = default
    )
    {
        ArgumentNullException.ThrowIfNull(scope);

        // Whole-file JSON has no index to push a predicate into, so the filters run here - but they
        // run BEFORE Skip/Take, which is the property that matters: a page trimmed first and
        // filtered second is short by however many rows the caller could not see, or was not asking
        // for. The authorization predicate and the presentation predicate are separate objects on
        // purpose (see ConversationListOptions); here they are simply two conjuncts.
        var all = await ReadAllMetadataAsync(ct).ConfigureAwait(false);
        var listOptions = options ?? ConversationListOptions.Default;

        return
        [
            .. listOptions.Order(all.Where(m => scope.Admits(m) && listOptions.Admits(m))).Skip(offset).Take(limit),
        ];
    }

    /// <inheritdoc />
    public async Task<int> StampUnownedThreadsAsync(string quarantineTenantId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(quarantineTenantId);

        var stamped = 0;

        foreach (var metadata in await ReadAllMetadataAsync(ct).ConfigureAwait(false))
        {
            if (metadata.TenantId is not null)
            {
                continue;
            }

            await UpdateMetadataAsync(
                    metadata.ThreadId,
                    existing => (existing ?? metadata) with { TenantId = quarantineTenantId },
                    ct
                )
                .ConfigureAwait(false);

            stamped++;
        }

        return stamped;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> ListThreadIdsByTenantAsync(
        string tenantId,
        IReadOnlyCollection<string>? threadIds,
        CancellationToken ct = default
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        var all = await ReadAllMetadataAsync(ct).ConfigureAwait(false);

        return
        [
            .. all.Where(m => string.Equals(m.TenantId, tenantId, StringComparison.Ordinal))
                .Where(m => threadIds is null || threadIds.Contains(m.ThreadId))
                .Select(m => m.ThreadId)
                .OrderBy(id => id, StringComparer.Ordinal),
        ];
    }

    /// <inheritdoc />
    public async Task<int> AdoptThreadsAsync(
        string fromTenantId,
        string toTenantId,
        string? ownerUserId,
        IReadOnlyCollection<string>? threadIds,
        CancellationToken ct = default
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fromTenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(toTenantId);

        var eligible = await ListThreadIdsByTenantAsync(fromTenantId, threadIds, ct).ConfigureAwait(false);

        foreach (var threadId in eligible)
        {
            await UpdateMetadataAsync(
                    threadId,
                    existing =>
                        existing is null
                            ? new ThreadMetadata
                            {
                                ThreadId = threadId,
                                LastUpdated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                                TenantId = toTenantId,
                                OwnerUserId = ownerUserId,
                            }
                            : existing with
                            {
                                TenantId = toTenantId,
                                OwnerUserId = ownerUserId ?? existing.OwnerUserId,
                            },
                    ct
                )
                .ConfigureAwait(false);
        }

        return eligible.Count;
    }

    /// <summary>
    /// Every thread's metadata, with the same minimal-entry fallback
    /// <see cref="ListThreadsAsync(int, int, ConversationListOptions, CancellationToken)"/> uses for a directory that has
    /// messages but no metadata file.
    /// </summary>
    private async Task<IReadOnlyList<ThreadMetadata>> ReadAllMetadataAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!Directory.Exists(_baseDirectory))
        {
            return [];
        }

        var metadataList = new List<ThreadMetadata>();

        // Each directory is a consistent read, not a snapshot of the entire corpus. Never retain
        // its gate while visiting another thread: historical scans must not stop unrelated writes.
        foreach (var dir in Directory.GetDirectories(_baseDirectory))
        {
            ct.ThrowIfCancellationRequested();
            var gate = GetDirectoryGate(dir);
            await gate.WaitAsync(ct);
            try
            {
                if (!Directory.Exists(dir))
                {
                    continue;
                }

                var threadId = Path.GetFileName(dir);
                var metadataFile = Path.Combine(dir, MetadataFileName);
                var metadata = await LoadMetadataFileAsync(metadataFile, ct);

                if (metadata != null)
                {
                    metadataList.Add(metadata);
                    continue;
                }

                var messagesFile = Path.Combine(dir, MessagesFileName);
                var messages = await LoadMessagesFromFileAsync(messagesFile, ct);
                metadataList.Add(
                    new ThreadMetadata
                    {
                        ThreadId = threadId,
                        LastUpdated =
                            messages.Count > 0
                                ? messages.Max(m => m.Timestamp)
                                : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    }
                );
            }
            finally
            {
                _ = gate.Release();
            }
        }

        return metadataList;
    }

    /// <inheritdoc />
    public async Task UpsertRunLedgerAsync(RunLedgerEntry entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var gate = GetDirectoryGate(GetThreadDirectory(entry.ThreadId));
        await gate.WaitAsync(ct);
        try
        {
            var threadDir = GetThreadDirectory(entry.ThreadId);
            _ = Directory.CreateDirectory(threadDir);

            var runsFile = Path.Combine(threadDir, RunsFileName);
            var runs = await LoadJsonFileAsync<List<RunLedgerEntry>>(runsFile, ct) ?? [];

            var idx = runs.FindIndex(r => r.RunId == entry.RunId);
            if (idx >= 0)
            {
                runs[idx] = entry;
            }
            else
            {
                runs.Add(entry);
            }

            await WriteJsonFileAsync(runsFile, runs, ct);
        }
        finally
        {
            _ = gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<RunLedgerEntry?> LoadRunLedgerAsync(string runId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(runId);

        ct.ThrowIfCancellationRequested();
        if (!Directory.Exists(_baseDirectory))
        {
            return null;
        }

        // No threadId-from-runId index exists on disk, so scan each thread's runs.json.
        foreach (var dir in Directory.GetDirectories(_baseDirectory))
        {
            ct.ThrowIfCancellationRequested();
            var gate = GetDirectoryGate(dir);
            await gate.WaitAsync(ct);
            try
            {
                var runsFile = Path.Combine(dir, RunsFileName);
                var runs = await LoadJsonFileAsync<List<RunLedgerEntry>>(runsFile, ct);
                var match = runs?.FirstOrDefault(r => r.RunId == runId);
                if (match != null)
                {
                    return match;
                }
            }
            finally
            {
                _ = gate.Release();
            }
        }

        return null;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RunLedgerEntry>> ListRunLedgerAsync(string threadId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(threadId);

        var gate = GetDirectoryGate(GetThreadDirectory(threadId));
        await gate.WaitAsync(ct);
        try
        {
            var runsFile = Path.Combine(GetThreadDirectory(threadId), RunsFileName);
            var runs = await LoadJsonFileAsync<List<RunLedgerEntry>>(runsFile, ct) ?? [];

            return [.. runs.OrderByDescending(r => r.CreatedAt)];
        }
        finally
        {
            _ = gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task RecordAcceptedInputAsync(
        string threadId,
        string inputId,
        DateTimeOffset acceptedAt,
        CancellationToken ct = default
    ) => _ = await WriteAcceptedInputAsync(threadId, inputId, acceptedAt, onlyIfAbsent: false, ct);

    /// <inheritdoc />
    /// <remarks>
    /// The directory gate makes this atomic against other callers on THIS store instance. It is not a
    /// cross-process reservation: two processes pointed at the same directory can both observe an absent
    /// entry and both write. That is the same last-writer-wins exposure the rest of this store already has
    /// (see the atomic JSON writer below), and single-writer is this store's documented deployment
    /// shape — a multi-process host wants the SQLite store, whose primary key arbitrates in the engine.
    /// </remarks>
    public async Task<bool> TryReserveAcceptedInputAsync(
        string threadId,
        string inputId,
        DateTimeOffset acceptedAt,
        CancellationToken ct = default
    ) => await WriteAcceptedInputAsync(threadId, inputId, acceptedAt, onlyIfAbsent: true, ct);

    /// <summary>
    /// Shared body of the record/reserve pair, so the two can never disagree about where the file lives or
    /// what an existing entry means.
    /// </summary>
    /// <param name="threadId">The thread the input was accepted for.</param>
    /// <param name="inputId">The input identifier.</param>
    /// <param name="acceptedAt">When the input was accepted.</param>
    /// <param name="onlyIfAbsent">
    /// When true an existing entry is left untouched and the call reports that it did not write.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if this call wrote the entry; false if one already existed and was left alone.</returns>
    private async Task<bool> WriteAcceptedInputAsync(
        string threadId,
        string inputId,
        DateTimeOffset acceptedAt,
        bool onlyIfAbsent,
        CancellationToken ct
    )
    {
        ArgumentNullException.ThrowIfNull(threadId);
        ArgumentNullException.ThrowIfNull(inputId);

        var gate = GetDirectoryGate(GetThreadDirectory(threadId));
        await gate.WaitAsync(ct);
        try
        {
            var threadDir = GetThreadDirectory(threadId);
            _ = Directory.CreateDirectory(threadDir);

            var acceptedFile = Path.Combine(threadDir, AcceptedInputsFileName);
            var accepted = await LoadJsonFileAsync<List<AcceptedInputEntry>>(acceptedFile, ct) ?? [];

            var idx = accepted.FindIndex(a => a.InputId == inputId);
            if (idx >= 0 && onlyIfAbsent)
            {
                return false;
            }

            var entry = new AcceptedInputEntry(threadId, inputId, acceptedAt);
            if (idx >= 0)
            {
                accepted[idx] = entry;
            }
            else
            {
                accepted.Add(entry);
            }

            await WriteJsonFileAsync(acceptedFile, accepted, ct);
            return true;
        }
        finally
        {
            _ = gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task RemoveAcceptedInputAsync(string threadId, string inputId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(threadId);
        ArgumentNullException.ThrowIfNull(inputId);

        var gate = GetDirectoryGate(GetThreadDirectory(threadId));
        await gate.WaitAsync(ct);
        try
        {
            var acceptedFile = Path.Combine(GetThreadDirectory(threadId), AcceptedInputsFileName);
            var accepted = await LoadJsonFileAsync<List<AcceptedInputEntry>>(acceptedFile, ct);
            if (accepted == null)
            {
                return;
            }

            if (accepted.RemoveAll(a => a.InputId == inputId) > 0)
            {
                await WriteJsonFileAsync(acceptedFile, accepted, ct);
            }
        }
        finally
        {
            _ = gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlySet<string>> ListAcceptedInputIdsAsync(string threadId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(threadId);

        var gate = GetDirectoryGate(GetThreadDirectory(threadId));
        await gate.WaitAsync(ct);
        try
        {
            var acceptedFile = Path.Combine(GetThreadDirectory(threadId), AcceptedInputsFileName);
            var accepted = await LoadJsonFileAsync<List<AcceptedInputEntry>>(acceptedFile, ct) ?? [];

            return accepted.Select(a => a.InputId).ToHashSet();
        }
        finally
        {
            _ = gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task RecordRunStartedAsync(RunLifecycleState state, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        RunLifecycleGuards.ValidateStart(state);

        var gate = GetDirectoryGate(GetThreadDirectory(state.ThreadId));
        await gate.WaitAsync(ct);
        try
        {
            var threadDir = GetThreadDirectory(state.ThreadId);
            _ = Directory.CreateDirectory(threadDir);

            var file = Path.Combine(threadDir, RunLifecycleFileName);
            var runs = await LoadJsonFileAsync<List<RunLifecycleState>>(file, ct) ?? [];

            var idx = runs.FindIndex(r => r.RunId == state.RunId);
            if (idx >= 0)
            {
                if (runs[idx].Phase == RunLifecyclePhase.Terminal)
                {
                    throw new InvalidOperationException(
                        $"Run '{state.RunId}' already reached a terminal boundary; it cannot be restarted."
                    );
                }

                // A re-record refreshes how the run describes itself; it does not roll back what
                // the run has already committed. Deferrals and turns are facts recorded after the
                // start, and SQLite's upsert leaves both alone — the three stores have to agree.
                runs[idx] = state with
                {
                    UpdatedAt = state.StartedAt,
                    TurnCount = runs[idx].TurnCount,
                    DeferredToolCalls = runs[idx].DeferredToolCalls,
                };
            }
            else
            {
                runs.Add(state with { UpdatedAt = state.StartedAt });
            }

            await WriteJsonFileAsync(file, runs, ct);
        }
        finally
        {
            _ = gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<RunLifecycleState?> LoadRunLifecycleAsync(string runId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(runId);

        var (found, result) = await WithRunAsync<RunLifecycleState>(
            runId,
            (_, runs, index) => Task.FromResult(runs[index]),
            ct
        );
        return found ? result : null;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RunLifecycleState>> ListRunLifecycleAsync(
        string threadId,
        CancellationToken ct = default
    )
    {
        ArgumentNullException.ThrowIfNull(threadId);

        var gate = GetDirectoryGate(GetThreadDirectory(threadId));
        await gate.WaitAsync(ct);
        try
        {
            var runs = await LoadThreadLifecycleAsync(threadId, ct);
            return [.. runs.OrderByDescending(r => r.StartedAt).ThenBy(r => r.RunId, StringComparer.Ordinal)];
        }
        finally
        {
            _ = gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RunLifecycleState>> ListNonTerminalRunsAsync(
        string threadId,
        CancellationToken ct = default
    )
    {
        ArgumentNullException.ThrowIfNull(threadId);

        var gate = GetDirectoryGate(GetThreadDirectory(threadId));
        await gate.WaitAsync(ct);
        try
        {
            var runs = await LoadThreadLifecycleAsync(threadId, ct);
            return
            [
                .. runs.Where(r => r.Phase == RunLifecyclePhase.Running)
                    .OrderBy(r => r.StartedAt)
                    .ThenBy(r => r.RunId, StringComparer.Ordinal),
            ];
        }
        finally
        {
            _ = gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<bool> TryMarkRunTerminalAsync(
        string runId,
        string outcome,
        int turnCount,
        DateTimeOffset terminalAt,
        CancellationToken ct = default
    )
    {
        ArgumentNullException.ThrowIfNull(runId);
        ArgumentException.ThrowIfNullOrEmpty(outcome);

        var (found, result) = await WithRunAsync(
            runId,
            async (file, runs, idx) =>
            {
                if (runs[idx].Phase == RunLifecyclePhase.Terminal)
                {
                    return false;
                }

                runs[idx] = runs[idx] with
                {
                    Phase = RunLifecyclePhase.Terminal,
                    Outcome = outcome,
                    TurnCount = turnCount,
                    TerminalAt = terminalAt,
                    UpdatedAt = terminalAt,
                };

                await WriteJsonFileAsync(file, runs, ct);
                return true;
            },
            ct
        );
        return found && result;
    }

    /// <inheritdoc />
    public async Task<DeferredToolCallRecord> RecordDeferredToolCallAsync(
        string runId,
        DeferredToolCallRecord record,
        CancellationToken ct = default
    )
    {
        ArgumentNullException.ThrowIfNull(runId);
        ArgumentNullException.ThrowIfNull(record);

        var (found, result) = await WithRunAsync(
            runId,
            async (file, runs, idx) =>
            {
                var state = runs[idx];
                var already = state.DeferredToolCalls.FirstOrDefault(d => d.ToolCallId == record.ToolCallId);
                if (already != null)
                {
                    return already;
                }

                var committed = record with { Ordinal = state.DeferredToolCalls.Count + 1 };
                runs[idx] = state with
                {
                    DeferredToolCalls = [.. state.DeferredToolCalls, committed],
                    UpdatedAt = record.DeferredAt,
                };

                await WriteJsonFileAsync(file, runs, ct);
                return committed;
            },
            ct
        );
        return found
            ? result
            : throw new InvalidOperationException(
                $"Run '{runId}' was never recorded as started; cannot record deferral " + $"'{record.ToolCallId}'."
            );
    }

    /// <inheritdoc />
    public async Task<DeferredResolutionOutcome> TryResolveDeferredToolCallAsync(
        string threadId,
        string toolCallId,
        string resolutionFingerprint,
        string? childRunId,
        DateTimeOffset resolvedAt,
        CancellationToken ct = default
    )
    {
        ArgumentNullException.ThrowIfNull(threadId);
        ArgumentNullException.ThrowIfNull(toolCallId);
        ArgumentException.ThrowIfNullOrEmpty(resolutionFingerprint);

        var gate = GetDirectoryGate(GetThreadDirectory(threadId));
        await gate.WaitAsync(ct);
        try
        {
            var file = Path.Combine(GetThreadDirectory(threadId), RunLifecycleFileName);
            var runs = await LoadJsonFileAsync<List<RunLifecycleState>>(file, ct) ?? [];

            for (var runIdx = 0; runIdx < runs.Count; runIdx++)
            {
                var callIdx = RunLifecycleGuards.IndexOfDeferral(runs[runIdx], toolCallId);
                if (callIdx < 0)
                {
                    continue;
                }

                var existing = runs[runIdx].DeferredToolCalls[callIdx];
                if (existing.IsResolved)
                {
                    return string.Equals(
                        existing.ResolutionFingerprint,
                        resolutionFingerprint,
                        StringComparison.Ordinal
                    )
                        ? DeferredResolutionOutcome.Duplicate
                        : DeferredResolutionOutcome.Conflict;
                }

                var updated = runs[runIdx].DeferredToolCalls.ToArray();
                updated[callIdx] = existing with
                {
                    ResolvedAt = resolvedAt,
                    ResolutionFingerprint = resolutionFingerprint,
                    ChildRunId = childRunId,
                };

                runs[runIdx] = runs[runIdx] with { DeferredToolCalls = updated, UpdatedAt = resolvedAt };

                await WriteJsonFileAsync(file, runs, ct);
                return DeferredResolutionOutcome.Resolved;
            }

            return DeferredResolutionOutcome.NotFound;
        }
        finally
        {
            _ = gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<string?> AttachDeferredChildRunAsync(
        string threadId,
        string toolCallId,
        string childRunId,
        DateTimeOffset attachedAt,
        CancellationToken ct = default
    )
    {
        ArgumentNullException.ThrowIfNull(threadId);
        ArgumentNullException.ThrowIfNull(toolCallId);
        ArgumentException.ThrowIfNullOrEmpty(childRunId);

        var gate = GetDirectoryGate(GetThreadDirectory(threadId));
        await gate.WaitAsync(ct);
        try
        {
            var file = Path.Combine(GetThreadDirectory(threadId), RunLifecycleFileName);
            var runs = await LoadJsonFileAsync<List<RunLifecycleState>>(file, ct) ?? [];

            for (var runIdx = 0; runIdx < runs.Count; runIdx++)
            {
                var callIdx = RunLifecycleGuards.IndexOfDeferral(runs[runIdx], toolCallId);
                if (callIdx < 0)
                {
                    continue;
                }

                var existing = runs[runIdx].DeferredToolCalls[callIdx];
                var (standing, needsWrite) = RunLifecycleGuards.ClassifyChildRunAttach(existing, childRunId);
                if (!needsWrite)
                {
                    return standing;
                }

                var updated = runs[runIdx].DeferredToolCalls.ToArray();
                updated[callIdx] = existing with { ChildRunId = childRunId };

                runs[runIdx] = runs[runIdx] with { DeferredToolCalls = updated, UpdatedAt = attachedAt };

                await WriteJsonFileAsync(file, runs, ct);
                return childRunId;
            }

            return null;
        }
        finally
        {
            _ = gate.Release();
        }
    }

    private async Task<List<RunLifecycleState>> LoadThreadLifecycleAsync(string threadId, CancellationToken ct)
    {
        var file = Path.Combine(GetThreadDirectory(threadId), RunLifecycleFileName);
        return await LoadJsonFileAsync<List<RunLifecycleState>>(file, ct) ?? [];
    }

    /// <summary>
    /// Finds a run and completes the operation while its directory gate is still held. Returning
    /// the deserialized list after releasing that gate would allow a stale read-modify-write.
    /// </summary>
    private async Task<(bool Found, T Result)> WithRunAsync<T>(
        string runId,
        Func<string, List<RunLifecycleState>, int, Task<T>> action,
        CancellationToken ct
    )
    {
        ct.ThrowIfCancellationRequested();
        if (!Directory.Exists(_baseDirectory))
        {
            return (false, default!);
        }

        foreach (var dir in Directory.GetDirectories(_baseDirectory))
        {
            ct.ThrowIfCancellationRequested();
            var gate = GetDirectoryGate(dir);
            await gate.WaitAsync(ct);
            try
            {
                var file = Path.Combine(dir, RunLifecycleFileName);
                var runs = await LoadJsonFileAsync<List<RunLifecycleState>>(file, ct);
                var idx = runs?.FindIndex(r => r.RunId == runId) ?? -1;
                if (idx >= 0)
                {
                    return (true, await action(file, runs!, idx));
                }
            }
            finally
            {
                _ = gate.Release();
            }
        }

        return (false, default!);
    }

    /// <inheritdoc />
    public async Task<InputAcceptance?> TryReserveAcceptanceAsync(
        InputAcceptance acceptance,
        CancellationToken ct = default
    )
    {
        ArgumentNullException.ThrowIfNull(acceptance);
        var acceptanceFile = GetAcceptanceFile(acceptance.ThreadId, acceptance.InputId, createDirectory: true);

        // Serialize before acquiring the gate; publication must expose only a complete record.
        var payload = JsonSerializer.SerializeToUtf8Bytes(acceptance, AcceptanceJsonOptions);

        // Bounded by the same settle budget the reader uses rather than by a count of tries: what has to be
        // waited out is a transient of the OS, not a fixed number of collisions. A record deleted while any
        // reader still holds it keeps its name on Windows in a delete-pending state that refuses every open,
        // and a machine under load holds that reader open for far longer than a handful of immediate retries
        // covers. Spending the budget instead lets the arbitration finish; a refusal that outlives it is a
        // real fault and is rethrown as itself.
        var started = _time.GetTimestamp();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var gate =
                    await OpenMutationGateAsync(acceptanceFile + MutationGateSuffix, ct)
                    ?? throw new IOException("The admission publication gate did not settle.");
                await using (gate)
                {
                    if (await ReadAcceptanceFileAsync(acceptanceFile, ct) is { } existing)
                    {
                        return existing;
                    }

                    // Unix FileShare locks are acquired after open creates the name. Staging and
                    // renaming under the same gate as outcome/release avoids publishing empty JSON
                    // and serializes independent store instances and processes. Every writer sharing
                    // this storage must use the gate: quiesce pre-gate writers before upgrading.
                    var tempFile = acceptanceFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    try
                    {
                        // Do not recreate a thread directory deleted while this reservation waits.
                        await File.WriteAllBytesAsync(tempFile, payload, ct);
                        ct.ThrowIfCancellationRequested();
                        File.Move(tempFile, acceptanceFile, overwrite: false);
                    }
                    finally
                    {
                        try
                        {
                            File.Delete(tempFile);
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            // A leftover staging file is not an admission; preserve the original failure.
                        }
                    }
                    return null;
                }
            }
            catch (IOException)
            {
                if (await ReadAcceptanceFileAsync(acceptanceFile, ct) is { } existing)
                {
                    return existing;
                }

                if (_time.GetElapsedTime(started) >= AcceptanceSettleTimeout)
                {
                    throw;
                }

                // Yield before re-attempting, exactly as the sibling arm below does. Reaching here means the
                // create was refused AND the read found nothing, and the read answers "nothing" immediately —
                // synchronously — when the directory or the name is simply gone. DirectoryNotFoundException
                // derives from IOException, so a thread directory deleted out from under a reserve in flight
                // (DeleteThreadAsync takes no lock this path honours, and the directory is created once above
                // rather than per attempt) lands here every single time with nothing to wait on: without this
                // delay the loop is a tight synchronous spin that pegs a core for the whole budget and never
                // observes cancellation. The budget still bounds it, and the refusal is still rethrown as
                // itself once spent.
                await Task.Delay(AcceptanceSettlePoll, _time, ct);
                continue;
            }
            catch (UnauthorizedAccessException) when (_time.GetElapsedTime(started) < AcceptanceSettleTimeout)
            {
                await Task.Delay(AcceptanceSettlePoll, _time, ct);
                continue;
            }
        }
    }

    /// <inheritdoc />
    public Task<InputAcceptance?> GetAcceptanceAsync(string threadId, string inputId, CancellationToken ct = default) =>
        ReadAcceptanceFileAsync(GetAcceptanceFile(threadId, inputId, createDirectory: false), ct);

    /// <inheritdoc />
    public Task<bool> TryRecordOutcomeAsync(InputAcceptance acceptance, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(acceptance);
        return TryMutateAcceptanceAsync(
            GetAcceptanceFile(acceptance.ThreadId, acceptance.InputId, createDirectory: false),
            acceptance.ReservationId,
            acceptance,
            ct
        );
    }

    /// <inheritdoc />
    public Task<bool> TryReleaseAcceptanceAsync(
        string threadId,
        string inputId,
        Guid reservationId,
        CancellationToken ct = default
    ) =>
        TryMutateAcceptanceAsync(
            GetAcceptanceFile(threadId, inputId, createDirectory: false),
            reservationId,
            replacement: null,
            ct
        );

    private async Task<bool> TryMutateAcceptanceAsync(
        string acceptanceFile,
        Guid reservationId,
        InputAcceptance? replacement,
        CancellationToken ct
    )
    {
        var gate = await OpenMutationGateAsync(acceptanceFile + MutationGateSuffix, ct);
        if (gate is null)
        {
            return false;
        }

        await using (gate)
        {
            if (
                await ReadAcceptanceFileAsync(acceptanceFile, ct) is not { } stored
                || stored.ReservationId != reservationId
            )
            {
                return false;
            }

            if (replacement == null)
            {
                File.Delete(acceptanceFile);
                return true;
            }

            await WriteJsonFileAsync(acceptanceFile, replacement, AcceptanceJsonOptions, ct);
            return true;
        }
    }

    /// <summary>
    /// Takes the exclusive gate that serializes mutations of one admission record, waiting out a holder
    /// that is merely mid-mutation rather than reporting the record as not this caller's.
    /// <para>
    /// Reservations, outcomes and retractions share this gate. A retraction deletes the record before
    /// its handle closes, so callers must wait for that handle rather than treating a transient lock as
    /// a permanent refusal. A gate still held once the settle budget is spent is a mutation genuinely
    /// in flight, and standing down is the right answer to that.
    /// </para>
    /// </summary>
    private async Task<FileStream?> OpenMutationGateAsync(string gateFile, CancellationToken ct)
    {
        var started = _time.GetTimestamp();
        while (true)
        {
            try
            {
                return new FileStream(
                    gateFile,
                    FileMode.OpenOrCreate,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.None
                );
            }
            catch (IOException) when (_time.GetElapsedTime(started) < AcceptanceSettleTimeout)
            {
                // Another mutation of this record holds the gate; it is released by a handle close.
            }
            catch (IOException)
            {
                return null;
            }

            await Task.Delay(AcceptanceSettlePoll, _time, ct);
        }
    }

    private async Task<InputAcceptance?> ReadAcceptanceFileAsync(string acceptanceFile, CancellationToken ct)
    {
        var started = _time.GetTimestamp();
        while (true)
        {
            try
            {
                await using var stream = new FileStream(
                    acceptanceFile,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    bufferSize: 4096,
                    useAsync: true
                );
                if (
                    stream.Length > 0
                    && await JsonSerializer.DeserializeAsync<InputAcceptance>(stream, AcceptanceJsonOptions, ct)
                        is { } record
                )
                {
                    return record;
                }
            }
            catch (FileNotFoundException)
            {
                return null;
            }
            catch (DirectoryNotFoundException)
            {
                return null;
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                // The exclusive claim exists but has not settled yet.
            }
            catch (UnauthorizedAccessException) when (_time.GetElapsedTime(started) < AcceptanceSettleTimeout)
            {
                // A Windows delete-pending name is transient; a real permission failure outlives the budget.
            }

            if (_time.GetElapsedTime(started) >= AcceptanceSettleTimeout)
            {
                throw new IOException($"The admission record '{acceptanceFile}' exists but never became readable.");
            }

            await Task.Delay(AcceptanceSettlePoll, _time, ct);
        }
    }

    private string GetAcceptanceFile(string threadId, string inputId, bool createDirectory)
    {
        var acceptancesDir = Path.Combine(GetThreadDirectory(threadId), AcceptancesDirectoryName);
        if (createDirectory)
        {
            _ = Directory.CreateDirectory(acceptancesDir);
        }

        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(inputId)));
        return Path.Combine(acceptancesDir, $"{digest}.json");
    }

    private SemaphoreSlim GetDirectoryGate(string directory) =>
        _directoryGates.GetOrAdd(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)),
            static _ => new SemaphoreSlim(1, 1)
        );

    private string GetThreadDirectory(string threadId)
    {
        // Sanitize thread ID for filesystem safety
        var safeThreadId = string.Join("_", threadId.Split(Path.GetInvalidFileNameChars()));
        return Path.Combine(_baseDirectory, safeThreadId);
    }

    private static async Task<List<PersistedMessage>> LoadMessagesFromFileAsync(string filePath, CancellationToken ct)
    {
        if (!File.Exists(filePath))
        {
            return [];
        }

        try
        {
            var json = await File.ReadAllTextAsync(filePath, ct);
            return JsonSerializer.Deserialize<List<PersistedMessage>>(json, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            // If file is corrupted, start fresh
            return [];
        }
    }

    private async Task<ThreadMetadata?> LoadMetadataFileAsync(string filePath, CancellationToken ct)
    {
        if (!_strictMetadataReads)
        {
            return await LoadJsonFileAsync<ThreadMetadata>(filePath, ct);
        }

        if (!File.Exists(filePath))
        {
            return null;
        }

        var json = await File.ReadAllTextAsync(filePath, ct);
        return JsonSerializer.Deserialize<ThreadMetadata>(json, JsonOptions)
            ?? throw new JsonException("Persisted thread metadata must be an object, not null.");
    }

    private static async Task<T?> LoadJsonFileAsync<T>(string filePath, CancellationToken ct)
        where T : class
    {
        if (!File.Exists(filePath))
        {
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(filePath, ct);
            return JsonSerializer.Deserialize<T>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static Task WriteJsonFileAsync<T>(string filePath, T data, CancellationToken ct) =>
        WriteJsonFileAsync(filePath, data, JsonOptions, ct);

    /// <summary>
    /// Atomic write through the shared <see cref="AtomicFile"/> helper: a unique staging file, then a
    /// rename over the target with a bounded retry, and cleanup of the staging file on any failure.
    /// <para>
    /// The directory gates cover none of that. They are per-INSTANCE, so they serialize nothing
    /// between two stores over one base directory — the shape
    /// <c>ConversationContextReportTests(kind:"file")</c> and <c>InputAcceptanceStoreTests</c> construct —
    /// and nothing at all across processes.
    /// </para>
    /// <para>
    /// Serializes to a string and writes it WITHOUT a byte-order mark, which is what this store has always
    /// written; <see cref="AtomicFile.WriteJsonAsync"/> emits one, so this path stages its own bytes rather
    /// than rewriting every existing thread file's encoding for no gain.
    /// </para>
    /// </summary>
    private static Task WriteJsonFileAsync<T>(
        string filePath,
        T data,
        JsonSerializerOptions options,
        CancellationToken ct
    )
    {
        // Serialize before staging so a serialization failure never creates a temp file to clean up.
        var json = JsonSerializer.Serialize(data, options);

        return AtomicFile.WriteAsync(filePath, (tempFile, token) => File.WriteAllTextAsync(tempFile, json, token), ct);
    }
}
