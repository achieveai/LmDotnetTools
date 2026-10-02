namespace AchieveAi.LmDotnetTools.Sandbox;

/// <summary>Terminal native command output without text decoding. Artifact downloads remain capped at 64 MiB each.</summary>
public sealed record SandboxCommandBytesResult
{
    public required int ExitCode { get; init; }
    public required byte[] StandardOutput { get; init; }
    public required byte[] StandardError { get; init; }
    public required string OperationId { get; init; }
    public bool OperationRecordReleased { get; init; }
}
