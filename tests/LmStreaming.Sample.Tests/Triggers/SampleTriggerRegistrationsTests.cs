using AchieveAi.LmDotnetTools.LmStreaming.Sample.Triggers;

namespace LmStreaming.Sample.Tests.Triggers;

public class SampleTriggerRegistrationsTests
{
    [Fact]
    public void Build_ReturnsTriggerOptions_WithNoDuplicateKinds()
    {
        var options = SampleTriggerRegistrations.Build(sandboxEnabled: true);
        var kinds = options.AdditionalRegistrations.Select(r => r.Kind).ToList();
        kinds.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Build_OmitsSubAgentKind_WhenAccessorNotSupplied()
    {
        var options = SampleTriggerRegistrations.Build(sandboxEnabled: false);
        options
            .AdditionalRegistrations.Select(r => r.Kind)
            .Should()
            .NotContain(SubAgentCompletionTriggerSource.KindName);
    }

    [Fact]
    public void Build_IncludesSubAgentKind_WhenAccessorSupplied()
    {
        var options = SampleTriggerRegistrations.Build(sandboxEnabled: false, subAgentManagerAccessor: () => null);
        options.AdditionalRegistrations.Select(r => r.Kind).Should().Contain(SubAgentCompletionTriggerSource.KindName);
    }

    private sealed class NeverExitObserver : IProcessExitObserver
    {
        public Task<ProcessExit> WaitForExitAsync(string handle, CancellationToken ct) =>
            new TaskCompletionSource<ProcessExit>().Task;

        public void ValidateHandle(string handle) { }
    }

    private sealed class NoopSink : AchieveAi.LmDotnetTools.LmMultiTurn.Triggers.ITriggerEventSink
    {
        public ValueTask FireAsync(
            AchieveAi.LmDotnetTools.LmMultiTurn.Triggers.TriggerFireEvent fire,
            CancellationToken cancellationToken
        ) => ValueTask.CompletedTask;
    }

    private static AchieveAi.LmDotnetTools.LmMultiTurn.Triggers.TriggerArmRequest ProcessArmRequest(
        string handle = "job1"
    ) =>
        new()
        {
            WaitId = "w1",
            Kind = ProcessTriggerSource.KindName,
            ArgsJson = JsonSerializer.Serialize(new { handle }),
            ArmedAt = DateTimeOffset.UtcNow,
            Deadline = DateTimeOffset.UtcNow.AddMinutes(5),
        };

    [Fact]
    public async Task Build_ProcessKind_UsesSuppliedObserver_SoArmingIsAccepted()
    {
        // #142: with a real observer supplied, the sandbox-gated process kind must be armable —
        // it no longer carries the Noop placeholder whose arm is rejected as "not wired".
        var options = SampleTriggerRegistrations.Build(
            sandboxEnabled: true,
            processExitObserver: new NeverExitObserver()
        );
        var source = options.AdditionalRegistrations.Single(r => r.Kind == ProcessTriggerSource.KindName).Source;

        var armed = await source.ArmAsync(ProcessArmRequest(), new NoopSink(), CancellationToken.None);
        await armed.DisposeAsync();
    }

    [Fact]
    public async Task Build_ProcessKind_WithoutObserver_KeepsFailFastNoopRejection()
    {
        var options = SampleTriggerRegistrations.Build(sandboxEnabled: true);
        var source = options.AdditionalRegistrations.Single(r => r.Kind == ProcessTriggerSource.KindName).Source;

        await FluentActions
            .Awaiting(async () => await source.ArmAsync(ProcessArmRequest(), new NoopSink(), CancellationToken.None))
            .Should()
            .ThrowAsync<ArgumentException>();
    }

    /// <summary>Never resolves — arming must be decided before any read happens.</summary>
    private sealed class PendingReader : ISandboxWaitFileReader
    {
        public Task<byte[]> ReadAsync(string relativePath, long? maxBytes, CancellationToken ct) =>
            new TaskCompletionSource<byte[]>().Task;
    }

    /// <summary>
    /// F-001 pin at the REGISTRATION route — the only path the runtime actually arms through: the
    /// sandbox-gated process registration backed by a real <see cref="SandboxProcessExitObserver"/>
    /// must reject an invalid handle synchronously from ArmAsync, not accept it into a silent
    /// park-to-TTL wait.
    /// </summary>
    [Fact]
    public void Build_ProcessKind_WithRealObserver_RejectsAnInvalidHandleAtArmTime()
    {
        var observer = new SandboxProcessExitObserver(new PendingReader(), TimeProvider.System);
        var options = SampleTriggerRegistrations.Build(sandboxEnabled: true, processExitObserver: observer);
        var source = options.AdditionalRegistrations.Single(r => r.Kind == ProcessTriggerSource.KindName).Source;

        Action act = () => _ = source.ArmAsync(ProcessArmRequest("bad/handle"), new NoopSink(), CancellationToken.None);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Build_ProcessKind_DescriptionTeachesTheWaitFileConvention()
    {
        // The model can only follow the handle convention if the registration text states it.
        var options = SampleTriggerRegistrations.Build(
            sandboxEnabled: true,
            processExitObserver: new NeverExitObserver()
        );
        var registration = options.AdditionalRegistrations.Single(r => r.Kind == ProcessTriggerSource.KindName);

        registration.Description.Should().Contain(SandboxProcessExitObserver.WaitRootRelativePath);
    }
}
