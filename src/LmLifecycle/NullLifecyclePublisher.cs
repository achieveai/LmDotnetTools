namespace AchieveAi.LmDotnetTools.LmLifecycle;

/// <summary>
/// The publisher used when lifecycle observation is switched off: it accepts every event and does
/// nothing with it.
/// </summary>
/// <remarks>
/// Lifecycle hooks are disabled by default, so this is what a host gets unless it opts in. Having a
/// real no-op instance means producers can publish unconditionally instead of guarding every call
/// site with a null check — the disabled path stays a single virtual call with no allocation and no
/// behavioral difference from the baseline.
/// </remarks>
public sealed class NullLifecyclePublisher : ILifecyclePublisher
{
    /// <summary>The shared instance. This type has no state, so one is enough.</summary>
    public static NullLifecyclePublisher Instance { get; } = new();

    private NullLifecyclePublisher() { }

    /// <inheritdoc />
    public ValueTask PublishAsync(
        LifecycleEventEnvelope envelope,
        CancellationToken cancellationToken = default
    ) => ValueTask.CompletedTask;
}
