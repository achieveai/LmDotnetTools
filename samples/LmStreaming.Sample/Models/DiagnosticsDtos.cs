namespace LmStreaming.Sample.Models;

/// <summary>
/// A single log entry sent from the browser client.
/// </summary>
public record ClientLogEntry(
    string? Level,
    string? Message,
    string? Timestamp,
    string? File,
    int? Line,
    string? Function,
    string? Component,
    object? Data);

/// <summary>
/// A batch of client log entries.
/// </summary>
public record ClientLogBatch(ClientLogEntry[] Entries);
