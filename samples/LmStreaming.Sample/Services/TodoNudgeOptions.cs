namespace LmStreaming.Sample.Services;

/// <summary>
///     Configuration for the todo board's nudges (#583 PR 6, design §7.1). One instance per host,
///     read from the <c>"TodoNudges"</c> configuration section.
/// </summary>
/// <remarks>
///     <para>
///         Defaults are the shipped decision of record: the assignment notice (N1) is ON — it is the
///         lead's dispatch flow, triggered by an explicit <c>assign-task</c> call, and cannot loop on
///         its own — while every stalled-agent nudge tier (N2 run-end, N3 idle-turns, N4 breakdown)
///         is OFF, because those restart agents and therefore spend money. The thresholds are admitted
///         guesses (the design says so); they ship as config to be tuned against real runs, not as
///         constants.
///     </para>
///     <para>
///         Reading is deliberately tolerant: a missing section, an empty section, or a malformed value
///         reads as the default — feature-off, never a throw. (The configuration binder is not used:
///         it does not enforce anything and an empty JSON array still creates a section, so hand-rolled
///         <c>TryParse</c> reads are the only shape whose failure mode is guaranteed to be "default".)
///     </para>
/// </remarks>
public sealed record TodoNudgeOptions
{
    /// <summary>The configuration section these options are read from.</summary>
    public const string SectionName = "TodoNudges";

    /// <summary>N1 — notify an agent when a task is assigned to it. On by default (dispatch flow).</summary>
    public bool AssignmentNoticeEnabled { get; init; } = true;

    /// <summary>N2 — nudge an agent whose run ended while it still owns unfinished tasks. Default OFF.</summary>
    public bool RunEndNudgeEnabled { get; init; }

    /// <summary>N3 — nudge an agent when no board change has happened for <see cref="IdleTurnThreshold" /> observed turns. Default OFF.</summary>
    public bool IdleTurnsNudgeEnabled { get; init; }

    /// <summary>N4 — nudge an assignee whose task has been in progress past <see cref="BreakdownAfterMinutes" /> with no sub-items. Default OFF.</summary>
    public bool BreakdownNudgeEnabled { get; init; }

    /// <summary>
    ///     Opt-in gate for nudging the ROOT conversation. Auto-restarting the user's main chat because
    ///     the board looks unfinished is surprising and rude, so by default only sub-agents are nudged
    ///     and any nudge whose target resolves to the root conversation is dropped.
    /// </summary>
    public bool NudgeRootConversation { get; init; }

    /// <summary>N3 threshold: observed turns without a board change before a nudge. A config guess (design §7.1).</summary>
    public int IdleTurnThreshold { get; init; } = 6;

    /// <summary>N4 threshold, in minutes of lease age on an undecomposed in-progress task. A config guess (design §7.1).</summary>
    public int BreakdownAfterMinutes { get; init; } = 20;

    /// <summary>Whether any budgeted stalled-agent tier (N2–N4) is enabled — the gate for the event pump.</summary>
    public bool AnyStallNudgeEnabled => RunEndNudgeEnabled || IdleTurnsNudgeEnabled || BreakdownNudgeEnabled;

    /// <summary>Whether the nudge service needs to exist at all.</summary>
    public bool AnyNudgeEnabled => AssignmentNoticeEnabled || AnyStallNudgeEnabled;

    /// <summary>
    ///     Reads the <see cref="SectionName" /> section tolerantly. A null configuration, a missing or
    ///     empty section, and malformed values all yield the corresponding defaults — this method never
    ///     throws on configuration content.
    /// </summary>
    public static TodoNudgeOptions FromConfiguration(IConfiguration? configuration)
    {
        var defaults = new TodoNudgeOptions();
        var section = configuration?.GetSection(SectionName);
        if (section is null)
        {
            return defaults;
        }

        return new TodoNudgeOptions
        {
            AssignmentNoticeEnabled = ReadBool(section, "AssignmentNoticeEnabled", defaults.AssignmentNoticeEnabled),
            RunEndNudgeEnabled = ReadBool(section, "RunEndNudgeEnabled", defaults.RunEndNudgeEnabled),
            IdleTurnsNudgeEnabled = ReadBool(section, "IdleTurnsNudgeEnabled", defaults.IdleTurnsNudgeEnabled),
            BreakdownNudgeEnabled = ReadBool(section, "BreakdownNudgeEnabled", defaults.BreakdownNudgeEnabled),
            NudgeRootConversation = ReadBool(section, "NudgeRootConversation", defaults.NudgeRootConversation),
            IdleTurnThreshold = ReadPositiveInt(section, "IdleTurnThreshold", defaults.IdleTurnThreshold),
            BreakdownAfterMinutes = ReadPositiveInt(section, "BreakdownAfterMinutes", defaults.BreakdownAfterMinutes),
        };
    }

    private static bool ReadBool(IConfiguration section, string key, bool fallback)
    {
        return bool.TryParse(section[key], out var parsed) ? parsed : fallback;
    }

    private static int ReadPositiveInt(IConfiguration section, string key, int fallback)
    {
        return int.TryParse(section[key], out var parsed) && parsed > 0 ? parsed : fallback;
    }
}
