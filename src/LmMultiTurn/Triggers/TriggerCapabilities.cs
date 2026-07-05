namespace AchieveAi.LmDotnetTools.LmMultiTurn.Triggers;

/// <summary>
/// Declares what a <see cref="ITriggerSource"/> can do, so the <c>Wait</c> tool contract can
/// describe each kind explicitly and stably rather than inferring capabilities at runtime.
/// </summary>
/// <param name="SupportsBlock">
/// The source can back a block-mode wait (park the run, resolve with the fire payload).
/// </param>
/// <param name="SupportsNotify">
/// The source can back a notify-mode wait (inject a message without parking). Reserved for a
/// follow-up — no source ships notify support in this release.
/// </param>
/// <param name="SupportsRestore">
/// A wait armed against this source can be safely re-armed after a process restart from its
/// persisted arming record. Sources that cannot (e.g. a one-shot external event) return false,
/// and a restored block wait against them resolves with <c>trigger_lost_on_restart</c>.
/// </param>
public sealed record TriggerCapabilities(
    bool SupportsBlock,
    bool SupportsNotify,
    bool SupportsRestore);
