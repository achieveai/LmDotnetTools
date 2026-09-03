namespace AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;

/// <summary>
/// What one spawn's construction cost, split into the two phases a fix could plausibly move.
/// </summary>
public sealed record SubAgentSpawnTiming
{
    /// <summary>The child's agent id.</summary>
    public required string AgentId { get; init; }

    /// <summary>Template name the child was spawned from.</summary>
    public required string Template { get; init; }

    /// <summary>
    /// Wall-clock milliseconds spent building the child's fresh <c>FunctionRegistry</c> and copying the
    /// filtered parent contracts into it.
    /// </summary>
    public required long ToolRegistryMs { get; init; }

    /// <summary>
    /// Wall-clock milliseconds spent resolving the child's conversation store and inheriting the parent
    /// thread's tenant/owner onto it - the per-spawn context fan-out.
    /// </summary>
    public required long ContextFanOutMs { get; init; }

    /// <summary>Wall-clock milliseconds for the whole construction, both phases included.</summary>
    public required long TotalMs { get; init; }

    /// <summary>How many parent tools the child actually inherited.</summary>
    public required int InheritedToolCount { get; init; }

    /// <summary>
    /// UTF-8 size of those tools' names and descriptions. Timings are machine-dependent and a fast box
    /// hides a growing catalog; this number does not move with the hardware, so it is the one a
    /// threshold can be set on.
    /// </summary>
    public required int ToolCatalogBytes { get; init; }

    /// <summary>
    /// True when this construction rebuilt a FINISHED agent (the restart path) rather than spawning a
    /// new one. Both pay the same cost; only this flag tells them apart.
    /// </summary>
    public bool Reconstructed { get; init; }
}

/// <summary>
/// The run-level roll-up of everything <see cref="SubAgentInstrumentation"/> observed. Every field is
/// emitted even at zero: a reader has to be able to tell "measured, and nothing happened" from "this
/// host never measured", and an omitted key reads as the latter.
/// </summary>
public sealed record SubAgentStartupWork
{
    /// <summary>Sub-agent constructions, reconstructions included.</summary>
    public int Spawns { get; init; }

    /// <summary>How many of <see cref="Spawns"/> rebuilt a finished agent rather than starting a new one.</summary>
    public int Reconstructions { get; init; }

    /// <summary>Summed <see cref="SubAgentSpawnTiming.ToolRegistryMs"/>.</summary>
    public long SpawnToolRegistryMs { get; init; }

    /// <summary>Summed <see cref="SubAgentSpawnTiming.ContextFanOutMs"/>.</summary>
    public long SpawnContextFanOutMs { get; init; }

    /// <summary>Summed <see cref="SubAgentSpawnTiming.TotalMs"/>.</summary>
    public long SpawnTotalMs { get; init; }

    /// <summary>
    /// How many times the spawn tool's template catalog was serialized into its description. This was
    /// once per turn the tool was advertised until #675 memoized the descriptor surface; it is now once
    /// per rebuild, i.e. per change to the template set or to spawn suppression. So this counter reads
    /// as cache MISSES, and the gap between it and the run's turn count is the saving #675 bought.
    /// Multiplied by <see cref="TemplateCatalogBytes"/> it is still the honest recurring cost — the
    /// recurrence is simply far rarer than it was.
    /// </summary>
    public int TemplateCatalogBuilds { get; init; }

    /// <summary>Summed UTF-8 size of those catalogs.</summary>
    public long TemplateCatalogBytes { get; init; }

    /// <summary>
    /// How many <c>GetAgents</c> responses were produced. Not necessarily full-directory since #675:
    /// the retained tail is capped, so <see cref="DirectoryListingEntries"/> counts the rows actually
    /// returned rather than the directory's size.
    /// </summary>
    public int DirectoryListings { get; init; }

    /// <summary>Agents described across those responses.</summary>
    public long DirectoryListingEntries { get; init; }

    /// <summary>Summed UTF-8 size of those responses.</summary>
    public long DirectoryListingBytes { get; init; }
}

/// <summary>
/// Opt-in measurement sink for the coordination work a run does on top of the model calls themselves:
/// per-spawn tool-registry construction and context fan-out, finished-agent reconstruction,
/// template-catalog serialization, and <c>GetAgents</c> responses.
/// </summary>
/// <remarks>
/// <para>
/// Measuring is not judging. None of these is asserted here to be a defect — the point is that a
/// number exists BEFORE a fix lands, so a later claim of improvement can be checked against it rather
/// than believed. The eval harness reads the snapshot back out of persisted thread metadata, which is
/// why it must be plain data with no live references.
/// </para>
/// <para>
/// Null on <see cref="SubAgentOptions.Instrumentation"/> by default: a host that does not opt in never
/// allocates this and every seam short-circuits, so unconfigured behavior is unchanged.
/// </para>
/// </remarks>
public sealed class SubAgentInstrumentation
{
    // One lock rather than Interlocked per field: the spawn list and the totals derived from it have to
    // agree (Snapshot must never report 3 spawns whose milliseconds only 2 have contributed to), and
    // these seams fire per spawn and per turn, never in a hot loop.
    private readonly object _gate = new();
    private readonly List<SubAgentSpawnTiming> _spawns = [];
    private int _reconstructions;
    private long _toolRegistryMs;
    private long _contextFanOutMs;
    private long _totalMs;
    private int _templateCatalogBuilds;
    private long _templateCatalogBytes;
    private int _directoryListings;
    private long _directoryListingEntries;
    private long _directoryListingBytes;

    /// <summary>
    /// Every spawn observed, in the order construction finished. Kept individually as well as summed
    /// because an outlier spawn and a uniformly slow run have the same total and different causes.
    /// </summary>
    public IReadOnlyList<SubAgentSpawnTiming> Spawns
    {
        get
        {
            lock (_gate)
            {
                return [.. _spawns];
            }
        }
    }

    /// <summary>Records one completed sub-agent construction.</summary>
    public void RecordSpawn(SubAgentSpawnTiming timing)
    {
        ArgumentNullException.ThrowIfNull(timing);

        lock (_gate)
        {
            _spawns.Add(timing);
            if (timing.Reconstructed)
            {
                _reconstructions++;
            }

            _toolRegistryMs += timing.ToolRegistryMs;
            _contextFanOutMs += timing.ContextFanOutMs;
            _totalMs += timing.TotalMs;
        }
    }

    /// <summary>Records one serialization of the spawn tool's template catalog.</summary>
    public void RecordTemplateCatalog(int bytes)
    {
        lock (_gate)
        {
            _templateCatalogBuilds++;
            _templateCatalogBytes += bytes;
        }
    }

    /// <summary>Records one full-directory listing response.</summary>
    public void RecordDirectoryListing(int entries, int bytes)
    {
        lock (_gate)
        {
            _directoryListings++;
            _directoryListingEntries += entries;
            _directoryListingBytes += bytes;
        }
    }

    /// <summary>Takes a consistent point-in-time roll-up of everything recorded so far.</summary>
    public SubAgentStartupWork Snapshot()
    {
        lock (_gate)
        {
            return new SubAgentStartupWork
            {
                Spawns = _spawns.Count,
                Reconstructions = _reconstructions,
                SpawnToolRegistryMs = _toolRegistryMs,
                SpawnContextFanOutMs = _contextFanOutMs,
                SpawnTotalMs = _totalMs,
                TemplateCatalogBuilds = _templateCatalogBuilds,
                TemplateCatalogBytes = _templateCatalogBytes,
                DirectoryListings = _directoryListings,
                DirectoryListingEntries = _directoryListingEntries,
                DirectoryListingBytes = _directoryListingBytes,
            };
        }
    }
}
