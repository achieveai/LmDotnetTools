namespace AchieveAi.LmDotnetTools.LmMultiTurn.Messages;

/// <summary>
/// Marks a <see cref="QueuedInput"/> that was injected by a notify-mode trigger fire rather than a
/// real user. The actual content rides in <c>Input.Messages</c> (a <c>&lt;trigger&gt;</c>-tagged
/// user message); this marker exists only for telemetry/log correlation, mirroring how
/// <c>ResumeSentinel</c> marks an internal resume. <see cref="IsError"/> is true for a failure
/// envelope (e.g. a source fault surfaced as a fire).
/// </summary>
public record TriggerEnvelope(bool IsError);
