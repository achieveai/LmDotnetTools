using System.Text;
using AchieveAi.LmDotnetTools.Sandbox.Command;
using FluentAssertions;
using Xunit;

namespace AchieveAi.LmDotnetTools.Sandbox.Tests.Command;

public class SandboxClientCommandTests
{
    private const string Session = "sess-1";

    private static byte[] Utf8(string value) => Encoding.UTF8.GetBytes(value);

    [Fact]
    public async Task ExecuteAsync_SmallOutput_ReturnsInlineResult_AndDeletesArtifactsImmediately()
    {
        var fake = new FakeSandboxGateway();
        using var client = CommandTestSupport.CreateClient(fake);
        var command = new SandboxCommand(["echo", "hi"], operationId: "op-1");
        var op = CommandTestSupport.OperationDirectory(Session, "op-1");
        fake.Program(op, exitCode: 0, stdout: Utf8("hello\n"), stderr: Utf8("warn\n"));

        var result = await client.ExecuteAsync(Session, command);

        result.ExitCode.Should().Be(0);
        result.StandardOutput.Should().Be("hello\n");
        result.StandardError.Should().Be("warn\n");
        result.CombinedOutput.Should().Be("hello\nwarn\n");
        result.OperationId.Should().Be("op-1");
        fake.SideEffectCount.Should().Be(1);
        fake.RunSubmissionCount.Should().Be(1);
        // Small streams are inlined in the manifest — no chunk reads needed.
        fake.Requests.Should().NotContain(r => r.Kind == CommandScriptKind.Read);
        // Verified success deletes the artifacts immediately.
        fake.CleanedOperations.Should().Contain(op);
    }

    [Fact]
    public async Task ExecuteAsync_GeneratedOperationId_IsSurfacedOnResult()
    {
        var fake = new FakeSandboxGateway();
        using var client = CommandTestSupport.CreateClient(fake);

        var result = await client.ExecuteAsync(Session, new SandboxCommand(["true"]));

        result.OperationId.Should().MatchRegex("^[0-9a-f]{32}$");
    }

    [Fact]
    public async Task ExecuteAsync_StdoutOver20Kb_ReassemblesExactBytesViaChunkedReads()
    {
        var payload = new byte[25_000];
        for (var i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)('A' + (i % 26));
        }

        var fake = new FakeSandboxGateway();
        using var client = CommandTestSupport.CreateClient(fake);
        var command = new SandboxCommand(["big"], operationId: "op-1");
        var op = CommandTestSupport.OperationDirectory(Session, "op-1");
        fake.Program(op, exitCode: 0, stdout: payload, stderr: []);

        var result = await client.ExecuteAsync(Session, command);

        Encoding.UTF8.GetBytes(result.StandardOutput).Should().Equal(payload);
        // Output above the inline threshold is read back in bounded chunks.
        fake.Requests.Count(r => r.Kind == CommandScriptKind.Read).Should().BeGreaterThan(1);
    }

    [Fact]
    public async Task ExecuteAsync_StderrOver500Lines_ReassemblesExactBytes()
    {
        var builder = new StringBuilder();
        for (var i = 0; i < 700; i++)
        {
            builder.Append("stderr diagnostic line ").Append(i.ToString("D4")).Append(" xyz\n");
        }

        var expected = builder.ToString();
        Encoding.UTF8.GetByteCount(expected).Should().BeGreaterThan(CommandArtifactLayout.InlineThresholdBytes);

        var fake = new FakeSandboxGateway();
        using var client = CommandTestSupport.CreateClient(fake);
        var command = new SandboxCommand(["noisy"], operationId: "op-1");
        var op = CommandTestSupport.OperationDirectory(Session, "op-1");
        fake.Program(op, exitCode: 3, stdout: [], stderr: Utf8(expected));

        var result = await client.ExecuteAsync(Session, command);

        result.ExitCode.Should().Be(3);
        result.StandardError.Should().Be(expected);
        result.StandardError.Split('\n').Should().HaveCount(701); // 700 lines + trailing empty
    }

    [Fact]
    public async Task ExecuteAsync_LostResponseAfterSideEffect_RecoversWithOneSubmissionAndOneSideEffect()
    {
        var fake = new FakeSandboxGateway();
        using var client = CommandTestSupport.CreateClient(fake, transportTimeout: TimeSpan.FromMilliseconds(300));
        var command = new SandboxCommand(["deploy"], operationId: "op-1");
        var op = CommandTestSupport.OperationDirectory(Session, "op-1");
        fake.Program(op, exitCode: 7, stdout: Utf8("done"), stderr: Utf8("note"));
        fake.SetRunMode(op, FakeSandboxGateway.RunMode.HangAfterCommit);

        var result = await client.ExecuteAsync(Session, command);

        result.ExitCode.Should().Be(7);
        result.StandardOutput.Should().Be("done");
        result.OperationId.Should().Be("op-1");
        // Exactly one side-effecting Bash submission, and the command ran exactly once.
        fake.Requests.Count(r => r.Kind == CommandScriptKind.Run).Should().Be(1);
        fake.SideEffectCount.Should().Be(1);
        // Recovery used idempotent probes only — no resubmission.
        fake.Requests.Should().Contain(r => r.Kind == CommandScriptKind.Probe);
    }

    [Fact]
    public async Task ExecuteAsync_TransportTimeoutWithoutSideEffect_ThrowsTransportTimeoutWithRecoverableOperationId()
    {
        var fake = new FakeSandboxGateway();
        // A short execution timeout keeps the deadline-based recovery poll bounded and fast when the
        // operation never materializes (the command may still be running up to the execution timeout).
        using var client = CommandTestSupport.CreateClient(
            fake,
            transportTimeout: TimeSpan.FromMilliseconds(200),
            executionTimeout: TimeSpan.FromMilliseconds(200)
        );
        var command = new SandboxCommand(["deploy"], operationId: "op-1");
        var op = CommandTestSupport.OperationDirectory(Session, "op-1");
        fake.SetRunMode(op, FakeSandboxGateway.RunMode.HangNoSideEffect);

        var exception = await Record.ExceptionAsync(() => client.ExecuteAsync(Session, command));

        exception.Should().BeOfType<SandboxException>();
        var sandboxException = (SandboxException)exception!;
        sandboxException.Kind.Should().Be(SandboxErrorKind.TransportTimeout);
        sandboxException.OperationId.Should().Be("op-1");
        fake.SideEffectCount.Should().Be(0);
        fake.Requests.Count(r => r.Kind == CommandScriptKind.Run).Should().Be(1);
        // Interrupted operation retains its artifacts (no clean issued).
        fake.CleanedOperations.Should().NotContain(op);
    }

    [Fact]
    public async Task ExecuteAsync_GatewayExecutionTimeout_ThrowsExecutionTimeout()
    {
        var fake = new FakeSandboxGateway();
        using var client = CommandTestSupport.CreateClient(fake);
        var command = new SandboxCommand(["slow"], operationId: "op-1");
        var op = CommandTestSupport.OperationDirectory(Session, "op-1");
        fake.SetRunMode(op, FakeSandboxGateway.RunMode.ExecutionTimeout);

        var exception = await Record.ExceptionAsync(() => client.ExecuteAsync(Session, command));

        exception.Should().BeOfType<SandboxException>();
        var sandboxException = (SandboxException)exception!;
        sandboxException.Kind.Should().Be(SandboxErrorKind.ExecutionTimeout);
        sandboxException.OperationId.Should().Be("op-1");
        fake.SideEffectCount.Should().Be(0);
    }

    [Fact]
    public async Task ExecuteAsync_CallerCancellation_ThrowsOperationCanceledException_NotSandboxException()
    {
        var fake = new FakeSandboxGateway();
        using var client = CommandTestSupport.CreateClient(fake, transportTimeout: TimeSpan.FromSeconds(30));
        var command = new SandboxCommand(["slow"], operationId: "op-1");
        var op = CommandTestSupport.OperationDirectory(Session, "op-1");
        fake.SetRunMode(op, FakeSandboxGateway.RunMode.HangNoSideEffect);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => client.ExecuteAsync(Session, command, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task ExecuteAsync_PendingThenPoll_RecoversResult()
    {
        var fake = new FakeSandboxGateway();
        using var client = CommandTestSupport.CreateClient(fake);
        var command = new SandboxCommand(["work"], operationId: "op-1");
        var op = CommandTestSupport.OperationDirectory(Session, "op-1");
        fake.Program(op, exitCode: 0, stdout: Utf8("polled-result"), stderr: []);
        fake.SetRunMode(op, FakeSandboxGateway.RunMode.PendingThenReady);

        var result = await client.ExecuteAsync(Session, command);

        result.StandardOutput.Should().Be("polled-result");
        fake.SideEffectCount.Should().Be(1);
        fake.Requests.Count(r => r.Kind == CommandScriptKind.Run).Should().Be(1);
        // The pending path drove at least one poll probe before reading the manifest.
        fake.Requests.Should().Contain(r => r.Kind == CommandScriptKind.Probe);
    }

    [Fact]
    public async Task ExecuteAsync_ConcurrentSameId_RunsOnce_BothRecoverSameResult()
    {
        var fake = new FakeSandboxGateway { SuppressClean = true };
        using var client = CommandTestSupport.CreateClient(fake);
        var op = CommandTestSupport.OperationDirectory(Session, "op-1");
        fake.Program(op, exitCode: 0, stdout: Utf8("shared-output"), stderr: []);

        var first = client.ExecuteAsync(Session, new SandboxCommand(["work"], operationId: "op-1"));
        var second = client.ExecuteAsync(Session, new SandboxCommand(["work"], operationId: "op-1"));
        var results = await Task.WhenAll(first, second);

        fake.SideEffectCount.Should().Be(1);
        results[0].StandardOutput.Should().Be("shared-output");
        results[1].StandardOutput.Should().Be("shared-output");
        results[0].ExitCode.Should().Be(results[1].ExitCode);
    }

    [Fact]
    public async Task ExecuteAsync_LaterCallWithSameId_RecoversRetainedResult_WithoutSubmitting()
    {
        var fake = new FakeSandboxGateway();
        using var client = CommandTestSupport.CreateClient(fake);
        var command = new SandboxCommand(["work"], operationId: "op-1");
        var op = CommandTestSupport.OperationDirectory(Session, "op-1");
        fake.SeedCompleted(
            op,
            CommandTestSupport.Digest(Session, command),
            exitCode: 5,
            stdout: Utf8("prior-run"),
            stderr: Utf8("prior-err")
        );

        var result = await client.ExecuteAsync(Session, command);

        result.ExitCode.Should().Be(5);
        result.StandardOutput.Should().Be("prior-run");
        result.StandardError.Should().Be("prior-err");
        // Recovered from the retained manifest — no side-effecting submission at all.
        fake.RunSubmissionCount.Should().Be(0);
        fake.SideEffectCount.Should().Be(0);
    }

    [Fact]
    public async Task ExecuteAsync_ReusedOperationIdWithDifferentCommand_ThrowsIntegrity_WithoutSubmitting()
    {
        var fake = new FakeSandboxGateway();
        using var client = CommandTestSupport.CreateClient(fake);
        var originalCommand = new SandboxCommand(["original"], operationId: "op-1");
        var op = CommandTestSupport.OperationDirectory(Session, "op-1");
        fake.SeedCompleted(
            op,
            CommandTestSupport.Digest(Session, originalCommand),
            exitCode: 0,
            stdout: Utf8("x"),
            stderr: []
        );

        var conflicting = new SandboxCommand(["different", "command"], operationId: "op-1");
        var exception = await Record.ExceptionAsync(() => client.ExecuteAsync(Session, conflicting));

        exception.Should().BeOfType<SandboxException>();
        var sandboxException = (SandboxException)exception!;
        sandboxException.Kind.Should().Be(SandboxErrorKind.Integrity);
        sandboxException.OperationId.Should().Be("op-1");
        // Digest mismatch is detected before any submission, and retains the existing artifacts.
        fake.RunSubmissionCount.Should().Be(0);
        fake.CleanedOperations.Should().NotContain(op);
    }

    [Fact]
    public async Task ExecuteAsync_NeverPutsCredentialInAnySubmittedScript()
    {
        var fake = new FakeSandboxGateway();
        using var client = CommandTestSupport.CreateClient(fake);
        var command = new SandboxCommand(["echo", "hi"], operationId: "op-1");
        var op = CommandTestSupport.OperationDirectory(Session, "op-1");
        fake.Program(op, exitCode: 0, stdout: Utf8("ok"), stderr: []);

        _ = await client.ExecuteAsync(Session, command);

        fake.RequestBodies.Should().NotBeEmpty();
        fake.RequestBodies.Should()
            .OnlyContain(body => !body.Contains(TestSupport.ValidSecret, StringComparison.Ordinal));
    }
}
