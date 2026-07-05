namespace AchieveAi.LmDotnetTools.LmMultiTurn.Triggers;

/// <summary>
/// A raw fact reported by a <see cref="ITriggerSource"/> when its observed event occurs. The
/// runtime decides whether this is the winning terminal transition for the wait and how to
/// deliver it — the source expresses no lifecycle policy.
/// </summary>
/// <param name="Payload">
/// Optional source-specific detail merged into the delivered result (size-capped by the runtime).
/// May be null when the fire itself is the only signal (e.g. a timer elapsing).
/// </param>
public sealed record TriggerFireEvent(string? Payload = null);
