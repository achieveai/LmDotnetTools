using System.Text.Json;
using System.Text.Json.Serialization;
using AchieveAi.LmDotnetTools.LmMultiTurn.Collaboration;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;

namespace AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;

/// <summary>
/// What a manager needs to register a finished sub-agent again after its parent's loop is rebuilt (#868):
/// how to rebuild the child, and what it last reported. Kept on the CHILD thread's metadata under
/// <see cref="Property"/>, so the parent's row stays small and each child has a single writer.
/// </summary>
/// <remarks>
/// <para>
/// A parent that is unloaded and rebuilt starts with an empty roster. Without this record its finished
/// sub-agents could no longer be messaged or listed, although their transcripts were still in the store.
/// Host-side rosters (the sample's and AWB's provenance stamps) carry neither the spawn arguments nor the
/// result, and they differ by host, so the SDK keeps its own.
/// </para>
/// <para>
/// Stored as a JSON string rather than nested values, through <see cref="ThreadMetadataProjection"/> like
/// the other projections in the bag. It carries a schema version: a record written by a newer build reads
/// as absent, so that child is skipped, and is never overwritten by this build's lifecycle writes.
/// </para>
/// </remarks>
internal sealed record SubAgentRecord
{
    /// <summary>Library-owned metadata key on the child thread.</summary>
    internal const string Property = "subagent_record";

    /// <summary>The persisted schema version this build writes.</summary>
    internal const int CurrentSchemaVersion = 1;

    /// <summary>The JSON member holding <see cref="SchemaVersion"/>.</summary>
    internal const string SchemaVersionPropertyName = "schema_version";

    /// <summary>
    /// Longest <see cref="LastResult"/> or <see cref="ErrorMessage"/> kept, in characters, marker included.
    /// The record lives in the metadata row every listing reads, and the full answer is still in the
    /// child's transcript.
    /// </summary>
    internal const int MaxLastResultLength = 16_384;

    /// <summary>Ends a <see cref="LastResult"/> cut to <see cref="MaxLastResultLength"/>.</summary>
    internal const string ResultTruncationMarker = "\n… [truncated; the full result is in the sub-agent's transcript]";

    /// <summary>Ends an <see cref="ErrorMessage"/> cut to <see cref="MaxLastResultLength"/>.</summary>
    internal const string ErrorTruncationMarker = "\n… [truncated; the full error text was not kept]";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>Schema version of this record.</summary>
    [JsonPropertyName(SchemaVersionPropertyName)]
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>
    /// The thread of the agent that spawned it. Not required: a parent with no thread id (a test double)
    /// still records its children, and the record must read back.
    /// </summary>
    public string? ParentThreadId { get; init; }

    public required string AgentId { get; init; }
    public string? Name { get; init; }
    public required string TemplateName { get; init; }
    public required string Task { get; init; }
    public string? ModelOverride { get; init; }
    public int? ModelIntelligence { get; init; }
    public string? AuthoritativeModelSelectionSource { get; init; }
    public string[]? AddTools { get; init; }
    public string[]? RemoveTools { get; init; }
    public string? ParentRunId { get; init; }
    public string? SpawningToolCallId { get; init; }
    public string? EffectiveModelId { get; init; }
    public int? EffectiveModelIntelligence { get; init; }
    public string? ModelSelectionSource { get; init; }

    /// <summary>The role the collaboration directory lists the child under; null without collaboration.</summary>
    public string? Role { get; init; }

    /// <summary>The description the collaboration directory lists the child with; null without collaboration.</summary>
    public string? Description { get; init; }

    /// <summary>The lifecycle status when the record was written.</summary>
    public required SubAgentStatus Status { get; init; }

    public DateTimeOffset? TerminalAtUtc { get; init; }
    public string? FailureCode { get; init; }
    public string? LastResult { get; init; }

    /// <summary>
    /// The message of the exception the finished run's completion holds, exactly as a waiter saw it; null
    /// when the run did not fail. Restored as that same message, so an errored child reads the same after
    /// its parent is rebuilt.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>The record describing <paramref name="state"/> as it is now.</summary>
    /// <param name="state">The sub-agent.</param>
    /// <param name="parentThreadId">The thread of the agent that spawned it.</param>
    /// <param name="collaboration">Its place in the collaboration directory, or null without one.</param>
    internal static SubAgentRecord From(
        SubAgentState state,
        string? parentThreadId,
        AgentCollaborationContext? collaboration
    ) =>
        new()
        {
            ParentThreadId = parentThreadId,
            AgentId = state.AgentId,
            Name = state.Name,
            TemplateName = state.TemplateName,
            Task = state.Task,
            ModelOverride = state.ModelOverride,
            ModelIntelligence = state.ModelIntelligence,
            AuthoritativeModelSelectionSource = state.AuthoritativeModelSelectionSource,
            AddTools = state.AddTools,
            RemoveTools = state.RemoveTools,
            ParentRunId = state.Lineage.ParentRunId,
            SpawningToolCallId = state.Lineage.SpawningToolCallId,
            EffectiveModelId = state.EffectiveModelId,
            EffectiveModelIntelligence = state.EffectiveModelIntelligence,
            ModelSelectionSource = state.ModelSelectionSource,
            Role = collaboration?.Role,
            Description = collaboration?.Description,
            Status = state.Status,
            TerminalAtUtc = state.TerminalAtUtc,
            FailureCode = state.FailureCode,
            LastResult = Cap(state.LastResult, ResultTruncationMarker),
            ErrorMessage = Cap(FaultMessage(state), ErrorTruncationMarker),
        };

    /// <summary>
    /// <paramref name="metadata"/> with this record set, keeping every other property. Unchanged when it
    /// holds a record a newer build wrote.
    /// </summary>
    internal ThreadMetadata WriteTo(ThreadMetadata metadata) =>
        IsFromANewerBuild(metadata)
            ? metadata
            : ThreadMetadataProjection.WithProjection(metadata, Property, JsonSerializer.Serialize(this, JsonOptions));

    /// <summary>
    /// The record on <paramref name="metadata"/>, or null when it has none or a newer build wrote it.
    /// </summary>
    /// <exception cref="JsonException">The record is present but cannot be read.</exception>
    internal static SubAgentRecord? Read(ThreadMetadata? metadata)
    {
        var json = ThreadMetadataProjection.RawJson(metadata, Property);
        if (json is null || IsFromANewerBuild(metadata))
        {
            return null;
        }

        return JsonSerializer.Deserialize<SubAgentRecord>(json, JsonOptions)
            ?? throw new JsonException($"The {Property} value is null.");
    }

    /// <summary>Whether <paramref name="metadata"/> holds a record with a schema version newer than this build's.</summary>
    internal static bool IsFromANewerBuild(ThreadMetadata? metadata) =>
        ThreadMetadataProjection.PersistedSchemaVersion(
            metadata,
            Property,
            SchemaVersionPropertyName,
            whenUnversioned: CurrentSchemaVersion
        ) > CurrentSchemaVersion;

    /// <summary>The message a waiter on the run's completion sees, or null when it did not fail.</summary>
    private static string? FaultMessage(SubAgentState state) =>
        state.Completion.Task is { IsFaulted: true, Exception: { } fault }
            ? (fault.InnerException ?? fault).Message
            : null;

    private static string? Cap(string? text, string marker) =>
        text is null || text.Length <= MaxLastResultLength
            ? text
            : string.Concat(text.AsSpan(0, MaxLastResultLength - marker.Length), marker);
}
