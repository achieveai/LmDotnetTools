using AchieveAi.LmDotnetTools.LmCore.Models;
using AchieveAi.LmDotnetTools.LmMultiTurn.UsageAccounting;

namespace AchieveAi.LmDotnetTools.LmMultiTurn.DualLayer;

/// <summary>
/// Forwards the executor's usage to a sink that exists only after the executor has been built,
/// namely the planner's usage ledger. The planner's registry needs the executor, so the executor
/// is built first. Records that arrive before <see cref="Target"/> is set are held, then replayed
/// once it is set.
/// </summary>
public sealed class DeferredUsageSink : IUsageSink
{
    private readonly object _lock = new();
    private readonly List<UsageRecord> _pending = [];
    private IUsageSink? _target;

    /// <summary>Where records go. Set once; earlier records are replayed in arrival order.</summary>
    public IUsageSink? Target
    {
        get => Volatile.Read(ref _target);
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            UsageRecord[] held;
            lock (_lock)
            {
                if (_target is not null)
                {
                    throw new InvalidOperationException("The usage target is already set.");
                }

                _target = value;
                held = [.. _pending];
                _pending.Clear();
            }

            foreach (var record in held)
            {
                value.RecordUsage(record);
            }
        }
    }

    /// <inheritdoc />
    public void RecordUsage(UsageRecord observation)
    {
        IUsageSink? target;
        lock (_lock)
        {
            target = _target;
            if (target is null)
            {
                _pending.Add(observation);
                return;
            }
        }

        target.RecordUsage(observation);
    }
}
