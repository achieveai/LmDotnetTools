using System.Collections.Concurrent;
using AchieveAi.LmDotnetTools.LmMultiTurn.Triggers;

namespace LmMultiTurn.Tests.Triggers;

/// <summary>
/// A trigger source whose <see cref="ArmAsync"/> hands back the event sink through <see cref="Sinks"/>
/// so a test can fire it on demand, keyed by wait id. Supports block AND notify so it backs both
/// <see cref="TriggerRuntimeTests"/>-style block scenarios and notify-mode tests (Tasks 3/4/5 reuse
/// this fake — do not re-declare it per test file).
/// </summary>
internal sealed class ManualTriggerSource : ITriggerSource
{
    public static TriggerCapabilities Caps { get; } =
        new(SupportsBlock: true, SupportsNotify: true, SupportsRestore: false);

    public readonly ConcurrentDictionary<string, ITriggerEventSink> Sinks = new();

    public ValueTask<IArmedTrigger> ArmAsync(
        TriggerArmRequest request, ITriggerEventSink eventSink, CancellationToken ct)
    {
        Sinks[request.WaitId] = eventSink;
        return ValueTask.FromResult<IArmedTrigger>(new Handle(request.WaitId, this));
    }

    private sealed class Handle(string waitId, ManualTriggerSource owner) : IArmedTrigger
    {
        public string WaitId { get; } = waitId;

        public ValueTask DisposeAsync()
        {
            owner.Sinks.TryRemove(WaitId, out _);
            return ValueTask.CompletedTask;
        }
    }
}
