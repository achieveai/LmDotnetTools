using System.Diagnostics;
using System.Text;
using AchieveAi.LmDotnetTools.Sandbox.Command;
using FluentAssertions;
using Xunit;

namespace AchieveAi.LmDotnetTools.Sandbox.Tests.Command;

/// <summary>
/// SDK-level proofs of the reliability/security hardening that is observable through
/// <see cref="SandboxClient.ExecuteAsync"/>: durable same-id idempotency after cleanup (finding #2),
/// operation-id preservation and manifest polling on ambiguous/failed post-RUN states plus bounded
/// idempotent read retry (finding #6), deadline-based bounded poll backoff (finding #7), strict UTF-8
/// decoding (finding #10), and redaction of unknown status tokens (finding #11). Each drives a
/// <see cref="FakeSandboxGateway"/> that models genuine artifact state, so the assertions are about real
/// behavior (one RUN, exact bytes, bounded timing) rather than call counts alone.
/// </summary>
public class CommandHardeningTests
{
    private const string Session = "sess-1";

    private static byte[] Utf8(string value) => Encoding.UTF8.GetBytes(value);

    private static int RunCount(FakeSandboxGateway fake) =>
        fake.Requests.Count(r => r.Kind == CommandScriptKind.Run);

    // ---- Finding #2: same-id reuse after successful cleanup never re-runs ----

    [Fact]
    public async Task ExecuteAsync_SameIdAfterSuccessfulCleanup_SmallOutput_ReplaysResult_WithNoSecondRun()
    {
        var fake = new FakeSandboxGateway();
        using var client = CommandTestSupport.CreateClient(fake);
        var op = CommandTestSupport.OperationDirectory(Session, "op-1");
        fake.Program(op, exitCode: 5, stdout: Utf8("small-out"), stderr: Utf8("small-err"));

        var first = await client.ExecuteAsync(Session, new SandboxCommand(["work"], operationId: "op-1"));
        var second = await client.ExecuteAsync(Session, new SandboxCommand(["work"], operationId: "op-1"));

        first.StandardOutput.Should().Be("small-out");
        second.ExitCode.Should().Be(5);
        second.StandardOutput.Should().Be("small-out");
        second.StandardError.Should().Be("small-err");
        // The command ran exactly once; the reused id was answered from the retained completion marker.
        fake.SideEffectCount.Should().Be(1);
        RunCount(fake).Should().Be(1, "the second call's pre-probe found the retained marker, so no RUN was submitted");
        fake.ReclaimedOperations.Should().Contain(op);
    }

    [Fact]
    public async Task ExecuteAsync_SameIdAfterSuccessfulCleanup_LargeOutput_RejectsWithoutRerunning()
    {
        var fake = new FakeSandboxGateway();
        using var client = CommandTestSupport.CreateClient(fake);
        var op = CommandTestSupport.OperationDirectory(Session, "op-1");
        var big = CommandTestSupport.PrintablePattern(30_000, seed: 1);
        fake.Program(op, exitCode: 0, stdout: big, stderr: []);

        var first = await client.ExecuteAsync(Session, new SandboxCommand(["work"], operationId: "op-1"));
        Encoding.UTF8.GetBytes(first.StandardOutput).Should().Equal(big);

        // Reclaim dropped the large output; a same-id replay must NOT re-run — it is rejected cleanly.
        var exception = await Record.ExceptionAsync(
            () => client.ExecuteAsync(Session, new SandboxCommand(["work"], operationId: "op-1"))
        );

        exception.Should().BeOfType<SandboxException>();
        var sandboxException = (SandboxException)exception!;
        sandboxException.Kind.Should().Be(SandboxErrorKind.Integrity);
        sandboxException.OperationId.Should().Be("op-1");
        fake.SideEffectCount.Should().Be(1);
        RunCount(fake).Should().Be(1);
    }

    // ---- Finding #6: OperationId preservation, malformed-RUN polling, idempotent read retry ----

    [Fact]
    public async Task ExecuteAsync_MalformedRunResponse_EntersManifestPolling_RecoversWithoutResubmitting()
    {
        var fake = new FakeSandboxGateway();
        using var client = CommandTestSupport.CreateClient(fake);
        var op = CommandTestSupport.OperationDirectory(Session, "op-1");
        fake.Program(op, exitCode: 0, stdout: Utf8("recovered"), stderr: []);
        fake.SetRunMode(op, FakeSandboxGateway.RunMode.GarbageTextAfterCommit);

        var result = await client.ExecuteAsync(Session, new SandboxCommand(["work"], operationId: "op-1"));

        result.StandardOutput.Should().Be("recovered");
        fake.SideEffectCount.Should().Be(1);
        RunCount(fake).Should().Be(1, "an ambiguous RUN body triggers polling, never a resubmission");
        fake.Requests.Should().Contain(r => r.Kind == CommandScriptKind.Probe);
    }

    [Fact]
    public async Task ExecuteAsync_RunResponseIs5xxAfterCommit_RecoversViaPolling_WithoutResubmitting()
    {
        var fake = new FakeSandboxGateway();
        using var client = CommandTestSupport.CreateClient(fake);
        var op = CommandTestSupport.OperationDirectory(Session, "op-1");
        fake.Program(op, exitCode: 4, stdout: Utf8("ran-then-lost"), stderr: []);
        // The gateway ran the command, then the RUN response was a 5xx (mapped to Protocol) — ambiguous.
        fake.SetRunHttpError(op, System.Net.HttpStatusCode.BadGateway, commitFirst: true);

        var result = await client.ExecuteAsync(Session, new SandboxCommand(["deploy"], operationId: "op-1"));

        result.ExitCode.Should().Be(4);
        result.StandardOutput.Should().Be("ran-then-lost");
        result.OperationId.Should().Be("op-1");
        fake.SideEffectCount.Should().Be(1);
        RunCount(fake).Should().Be(1, "a 5xx after the command ran must poll the manifest, never resubmit RUN");
        fake.Requests.Should().Contain(r => r.Kind == CommandScriptKind.Probe);
    }

    [Fact]
    public async Task ExecuteAsync_RunResponseIs5xxWithoutSideEffect_ThrowsWithRecoverableOperationId_NoResubmit()
    {
        var fake = new FakeSandboxGateway();
        using var client = CommandTestSupport.CreateClient(
            fake,
            executionTimeout: TimeSpan.FromMilliseconds(200)
        );
        var op = CommandTestSupport.OperationDirectory(Session, "op-1");
        fake.SetRunHttpError(op, System.Net.HttpStatusCode.ServiceUnavailable, commitFirst: false);

        var exception = await Record.ExceptionAsync(
            () => client.ExecuteAsync(Session, new SandboxCommand(["deploy"], operationId: "op-1"))
        );

        exception.Should().BeOfType<SandboxException>();
        // The command did not run, but the ambiguous failure still carries the recoverable id and never
        // resubmits the RUN.
        ((SandboxException)exception!).OperationId.Should().Be("op-1");
        fake.SideEffectCount.Should().Be(0);
        RunCount(fake).Should().Be(1);
    }

    [Fact]
    public async Task ExecuteAsync_TransientReadFailures_RetryFromHighestVerifiedOffset_ReassembleExactBytes()
    {
        var fake = new FakeSandboxGateway();
        using var client = CommandTestSupport.CreateClient(fake);
        var op = CommandTestSupport.OperationDirectory(Session, "op-1");
        var big = CommandTestSupport.PrintablePattern(30_000, seed: 2);
        fake.Program(op, exitCode: 0, stdout: big, stderr: []);
        // The first two chunk reads fail transiently; the idempotent retry must resume, never resubmit RUN.
        fake.FailReadsBeforeSuccess(op, 2);

        var result = await client.ExecuteAsync(Session, new SandboxCommand(["read"], operationId: "op-1"));

        Encoding.UTF8.GetBytes(result.StandardOutput).Should().Equal(big);
        RunCount(fake).Should().Be(1);
    }

    [Fact]
    public async Task ExecuteAsync_ReadFailuresBeyondRetryLimit_ThrowWithRecoverableOperationId_NeverResubmittingRun()
    {
        var fake = new FakeSandboxGateway();
        using var client = CommandTestSupport.CreateClient(fake);
        var op = CommandTestSupport.OperationDirectory(Session, "op-1");
        fake.Program(op, exitCode: 0, stdout: CommandTestSupport.PrintablePattern(30_000, seed: 3), stderr: []);
        fake.FailReadsBeforeSuccess(op, 100);

        var exception = await Record.ExceptionAsync(
            () => client.ExecuteAsync(Session, new SandboxCommand(["read"], operationId: "op-1"))
        );

        exception.Should().BeOfType<SandboxException>();
        ((SandboxException)exception!).OperationId.Should().Be("op-1");
        RunCount(fake).Should().Be(1);
    }

    // ---- Finding #7: deadline-based bounded backoff, not a fixed 500ms window ----

    [Fact]
    public async Task ExecuteAsync_SustainedPendingLongerThan500ms_StillRecovers_NoSpuriousTimeout()
    {
        var fake = new FakeSandboxGateway();
        using var client = CommandTestSupport.CreateClient(fake);
        var op = CommandTestSupport.OperationDirectory(Session, "op-1");
        fake.Program(op, exitCode: 0, stdout: Utf8("eventually"), stderr: []);
        fake.SetSustainedPending(op, TimeSpan.FromMilliseconds(700));

        var stopwatch = Stopwatch.StartNew();
        var result = await client.ExecuteAsync(Session, new SandboxCommand(["slow"], operationId: "op-1"));
        stopwatch.Stop();

        result.StandardOutput.Should().Be("eventually");
        stopwatch
            .Elapsed.Should()
            .BeGreaterThan(
                TimeSpan.FromMilliseconds(500),
                "the poll window is deadline-based and must outlast the old fixed 20x25ms=500ms cap"
            );
        fake.SideEffectCount.Should().Be(1);
    }

    [Fact]
    public async Task ExecuteAsync_PendingNeverCompletes_TimesOutDeterministically_Bounded_WithRecoverableOperationId()
    {
        var fake = new FakeSandboxGateway();
        using var client = CommandTestSupport.CreateClient(fake, executionTimeout: TimeSpan.FromMilliseconds(200));
        var op = CommandTestSupport.OperationDirectory(Session, "op-1");
        fake.SetRunMode(op, FakeSandboxGateway.RunMode.PendingNeverReady);

        var stopwatch = Stopwatch.StartNew();
        var exception = await Record.ExceptionAsync(
            () => client.ExecuteAsync(Session, new SandboxCommand(["hang"], operationId: "op-1"))
        );
        stopwatch.Stop();

        exception.Should().BeOfType<SandboxException>();
        var sandboxException = (SandboxException)exception!;
        sandboxException.Kind.Should().Be(SandboxErrorKind.TransportTimeout);
        sandboxException.OperationId.Should().Be("op-1");
        stopwatch
            .Elapsed.Should()
            .BeLessThan(TimeSpan.FromSeconds(10), "the poll is bounded by the execution timeout plus grace, never unbounded");
        fake.SideEffectCount.Should().Be(0);
    }

    // ---- Finding #10: strict UTF-8 decoding of materialized output ----

    [Fact]
    public async Task ExecuteAsync_NonUtf8Output_FailsIntegrity_WithOperationId_AndNoReplacementCharacter()
    {
        var fake = new FakeSandboxGateway();
        using var client = CommandTestSupport.CreateClient(fake);
        var op = CommandTestSupport.OperationDirectory(Session, "op-1");
        var invalid = new byte[] { 0x41, 0xff, 0xfe, 0x80, 0x42 };
        fake.Program(op, exitCode: 0, stdout: invalid, stderr: []);

        var exception = await Record.ExceptionAsync(
            () => client.ExecuteAsync(Session, new SandboxCommand(["binary"], operationId: "op-1"))
        );

        exception.Should().BeOfType<SandboxException>();
        var sandboxException = (SandboxException)exception!;
        sandboxException.Kind.Should().Be(SandboxErrorKind.Integrity);
        sandboxException.OperationId.Should().Be("op-1");
        sandboxException.Message.Should().NotContain("\uFFFD");
    }

    // ---- Finding #9 (end-to-end): malformed manifest maps only to Protocol, never a raw exception ----

    [Fact]
    public async Task ExecuteAsync_ManifestWithUnparsableJson_MapsToProtocol_WithOperationId()
    {
        var fake = new FakeSandboxGateway();
        using var client = CommandTestSupport.CreateClient(fake);
        var op = CommandTestSupport.OperationDirectory(Session, "op-1");
        fake.SeedRawManifestJson(op, "{ this is not valid manifest json ]");

        var exception = await Record.ExceptionAsync(
            () => client.ExecuteAsync(Session, new SandboxCommand(["x"], operationId: "op-1"))
        );

        exception.Should().BeOfType<SandboxException>();
        ((SandboxException)exception!).Kind.Should().Be(SandboxErrorKind.Protocol);
        ((SandboxException)exception!).OperationId.Should().Be("op-1");
    }

    [Fact]
    public async Task ExecuteAsync_ManifestWithUnsupportedVersion_MapsToProtocol_WithOperationId()
    {
        var fake = new FakeSandboxGateway();
        using var client = CommandTestSupport.CreateClient(fake);
        var op = CommandTestSupport.OperationDirectory(Session, "op-1");
        var json =
            "{\"v\":2,\"digest\":\""
            + new string('a', 64)
            + "\",\"exit\":0,\"stdout\":{\"len\":0,\"sha256\":\""
            + new string('b', 64)
            + "\",\"inline\":\"\"},\"stderr\":{\"len\":0,\"sha256\":\""
            + new string('c', 64)
            + "\",\"inline\":\"\"},\"lease\":1,\"created\":1}";
        fake.SeedRawManifestJson(op, json);

        var exception = await Record.ExceptionAsync(
            () => client.ExecuteAsync(Session, new SandboxCommand(["x"], operationId: "op-1"))
        );

        exception.Should().BeOfType<SandboxException>();
        ((SandboxException)exception!).Kind.Should().Be(SandboxErrorKind.Protocol);
    }

    // ---- Finding #11: unknown sentinel/status tokens are never echoed into an exception message ----

    [Fact]
    public async Task ExecuteAsync_UnknownSentinelStatus_ThrowsProtocol_WithoutEchoingTheRawStatusToken()
    {
        var fake = new FakeSandboxGateway();
        using var client = CommandTestSupport.CreateClient(fake);
        var op = CommandTestSupport.OperationDirectory(Session, "op-1");
        fake.SetRawStatusLine(op, $"{CommandSentinel.Marker} WEIRD-STATUS super-secret-payload");

        var exception = await Record.ExceptionAsync(
            () => client.ExecuteAsync(Session, new SandboxCommand(["x"], operationId: "op-1"))
        );

        exception.Should().BeOfType<SandboxException>();
        var sandboxException = (SandboxException)exception!;
        sandboxException.Kind.Should().Be(SandboxErrorKind.Protocol);
        sandboxException.OperationId.Should().Be("op-1");
        sandboxException
            .Message.Should()
            .NotContain("WEIRD-STATUS")
            .And.NotContain("super-secret-payload");
    }
}
