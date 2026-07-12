using AchieveAi.LmDotnetTools.Sandbox.Command;

namespace AchieveAi.LmDotnetTools.Sandbox.Tests.Command;

/// <summary>Shared helpers for command tests: a client wired over a <see cref="FakeSandboxGateway"/>, and the derived operation identifiers.</summary>
internal static class CommandTestSupport
{
    public static readonly TimeSpan ExecutionTimeout = TimeSpan.FromSeconds(120);

    /// <summary>The whole-second execution timeout the SDK derives from <see cref="ExecutionTimeout"/> (must match <c>GatewayExecutionTimeoutSeconds</c>).</summary>
    public const long ExecutionSeconds = 120;

    public static SandboxClient CreateClient(
        FakeSandboxGateway fake,
        TimeSpan? transportTimeout = null,
        TimeSpan? executionTimeout = null
    )
    {
        var serverAddress = TestSupport.NewLoopbackAddress();
        var httpClient = new HttpClient(fake) { BaseAddress = serverAddress };
        var options = new SandboxClientOptions(
            serverAddress,
            "app-1",
            TestSupport.ValidSecret,
            executionTimeout ?? ExecutionTimeout,
            transportTimeout ?? TimeSpan.FromSeconds(30)
        );
        return new SandboxClient(options, httpClient);
    }

    public static string OperationDirectory(string sessionId, string operationId) =>
        CommandOperation.OperationDirectoryName(sessionId, operationId);

    public static string Digest(string sessionId, SandboxCommand command) =>
        CommandOperation.CanonicalDigest(
            sessionId,
            command.Arguments,
            command.NormalizedWorkingDirectory,
            ExecutionSeconds
        );

    /// <summary>
    /// Deterministic printable-ASCII payload of <paramref name="length"/> bytes. Every byte is a
    /// printable ASCII character, so the payload round-trips through UTF-8 and exact-byte comparison of a
    /// reassembled stream is meaningful; <paramref name="seed"/> shifts the pattern so two streams differ.
    /// </summary>
    public static byte[] PrintablePattern(int length, int seed)
    {
        var bytes = new byte[length];
        for (var i = 0; i < length; i++)
        {
            bytes[i] = (byte)(32 + ((i + (seed * 7)) % 95));
        }

        return bytes;
    }
}
