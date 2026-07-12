using AchieveAi.LmDotnetTools.Sandbox.Command;

namespace AchieveAi.LmDotnetTools.Sandbox.Tests.Command;

/// <summary>Shared helpers for command tests: a client wired over a <see cref="FakeSandboxGateway"/>, and the derived operation identifiers.</summary>
internal static class CommandTestSupport
{
    public static readonly TimeSpan ExecutionTimeout = TimeSpan.FromSeconds(120);

    /// <summary>The whole-second execution timeout the SDK derives from <see cref="ExecutionTimeout"/> (must match <c>GatewayExecutionTimeoutSeconds</c>).</summary>
    public const long ExecutionSeconds = 120;

    public static SandboxClient CreateClient(FakeSandboxGateway fake, TimeSpan? transportTimeout = null)
    {
        var serverAddress = TestSupport.NewLoopbackAddress();
        var httpClient = new HttpClient(fake) { BaseAddress = serverAddress };
        var options = new SandboxClientOptions(
            serverAddress,
            "app-1",
            TestSupport.ValidSecret,
            ExecutionTimeout,
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
}
