using System.Text;
using AchieveAi.LmDotnetTools.Sandbox.Command;
using FluentAssertions;
using Xunit;

namespace AchieveAi.LmDotnetTools.Sandbox.Tests.Command;

/// <summary>
/// F2: an abandoned expired claim (a submitter that crashed after claiming, leaving no manifest) must
/// SELF-RECOVER on a same-id retry. The guarded RUN re-elects exactly one claimant and runs the command
/// once — without waiting for the 24h stale sweep and without depending on any unrelated successful
/// command. A still-active/pending operation is never resubmitted: the retry polls the manifest and, if
/// it never completes, times out carrying the recoverable operation id.
/// </summary>
public class CommandSelfRecoveryTests
{
    private const string Session = "sess-1";

    private static byte[] Utf8(string value) => Encoding.UTF8.GetBytes(value);

    private static int RunCount(FakeSandboxGateway fake) =>
        fake.Requests.Count(r => r.Kind == CommandScriptKind.Run);

    [Fact]
    public async Task ExecuteAsync_AbandonedExpiredClaim_SelfRecovers_RunsExactlyOnce_NoUnrelatedCommand()
    {
        var fake = new FakeSandboxGateway();
        using var client = CommandTestSupport.CreateClient(fake);
        var op = CommandTestSupport.OperationDirectory(Session, "op-1");
        fake.Program(op, exitCode: 3, stdout: Utf8("recovered-run"), stderr: Utf8("recovered-err"));
        // A prior submitter crashed after claiming, leaving the claim with no manifest.
        fake.SeedAbandonedClaim(op);

        var result = await client.ExecuteAsync(Session, new SandboxCommand(["work"], operationId: "op-1"));

        result.ExitCode.Should().Be(3);
        result.StandardOutput.Should().Be("recovered-run");
        result.StandardError.Should().Be("recovered-err");
        // This same-id retry alone recovered the abandoned claim: exactly one submission, one run.
        fake.SideEffectCount.Should().Be(1);
        RunCount(fake).Should().Be(1);
        // It observed the abandoned claim as PENDING (pre-probe) before the single guarded RUN.
        fake.Requests.Should().Contain(r => r.Kind == CommandScriptKind.Probe);
    }

    [Fact]
    public async Task ExecuteAsync_StillActiveClaim_IsNeverResubmitted_PollsThenTimesOutWithRecoverableId()
    {
        var fake = new FakeSandboxGateway();
        using var client = CommandTestSupport.CreateClient(
            fake,
            executionTimeout: TimeSpan.FromMilliseconds(200)
        );
        var op = CommandTestSupport.OperationDirectory(Session, "op-1");
        // A live peer holds the claim and never commits — an ACTIVE (not abandoned) operation that must
        // never be recovered/resubmitted.
        fake.SetRunMode(op, FakeSandboxGateway.RunMode.PendingNeverReady);

        var exception = await Record.ExceptionAsync(
            () => client.ExecuteAsync(Session, new SandboxCommand(["work"], operationId: "op-1"))
        );

        exception.Should().BeOfType<SandboxException>();
        var sandboxException = (SandboxException)exception!;
        sandboxException.Kind.Should().Be(SandboxErrorKind.TransportTimeout);
        sandboxException.OperationId.Should().Be("op-1");
        // Exactly one RUN, no side effect: the active claim was polled, never resubmitted or recovered.
        RunCount(fake).Should().Be(1);
        fake.SideEffectCount.Should().Be(0);
    }
}
