namespace AchieveAi.LmDotnetTools.LmCore.Messages;

/// <summary>
///     How a <see cref="CompactionCheckpointMessage" /> is rendered into the synthetic user turn the
///     agent projection dispatches in place of the compacted rows (#683; spec 679 §2.3).
/// </summary>
/// <remarks>
///     The envelope's prose is versioned with the checkpoint's <c>SchemaVersion</c>; these options only
///     choose the host-dependent trailer. The recall hint names a tool, and a hint naming a tool the loop
///     did not register would send the model after something it cannot call, so it is off until the host
///     that registers the tool says so.
/// </remarks>
public sealed record CheckpointRenderOptions
{
    /// <summary>No recall hint: what the durable row's <see cref="CompactionCheckpointMessage.Text" /> uses.</summary>
    public static readonly CheckpointRenderOptions Default = new();

    /// <summary>
    ///     The name of the tool that reads compacted rows back, when the loop registered one. The envelope
    ///     then ends with the one-line instruction to use it (spec 679 §6.4); null omits the line.
    /// </summary>
    public string? RecallToolName { get; init; }
}
