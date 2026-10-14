namespace AchieveAi.LmDotnetTools.Sandbox;

/// <summary>Which pipe produced a streamed command output chunk.</summary>
public enum SandboxOutputStream
{
    Stdout,
    Stderr,
}

/// <summary>One byte-exact chunk from a sandbox process. The bytes remain valid after the callback returns.</summary>
public sealed record SandboxOutputChunk(SandboxOutputStream Stream, ReadOnlyMemory<byte> Data);

/// <summary>Terminal status of a streamed command. A nonzero exit code is a completed process result.</summary>
public sealed record SandboxStreamResult(int ExitCode, long StdoutBytes, long StderrBytes);
