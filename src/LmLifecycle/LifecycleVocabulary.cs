namespace AchieveAi.LmDotnetTools.LmLifecycle;

/// <summary>
/// Terminal outcomes a run can report.
/// </summary>
/// <remarks>Open vocabulary — an unrecognized value is preserved, not rejected.</remarks>
public static class LifecycleRunOutcomes
{
    /// <summary>The run finished normally.</summary>
    public const string Completed = "completed";

    /// <summary>The run ended because of an error.</summary>
    public const string Error = "error";

    /// <summary>The run was cancelled by its caller.</summary>
    public const string Cancelled = "cancelled";

    /// <summary>The run was interrupted by the provider or the host.</summary>
    public const string Interrupted = "interrupted";

    /// <summary>The run stopped because it reached its configured turn ceiling.</summary>
    public const string MaxTurns = "max_turns";

    /// <summary>
    /// The run was a delayed-result child that deliberately performed no model turn because a
    /// sibling result from the same batch was still outstanding.
    /// </summary>
    /// <remarks>
    /// Exactly one child in a batch continues the conversation; every other sibling completes with
    /// this outcome and zero model turns. See ADR 0004.
    /// </remarks>
    public const string AwaitingSiblingResults = "awaiting_sibling_results";
}

/// <summary>
/// Terminal outcomes a turn can report.
/// </summary>
/// <remarks>Open vocabulary — an unrecognized value is preserved, not rejected.</remarks>
public static class LifecycleTurnOutcomes
{
    /// <summary>The turn produced a final assistant response.</summary>
    public const string Completed = "completed";

    /// <summary>The turn ended because of an error.</summary>
    public const string Error = "error";

    /// <summary>The turn was cancelled by its caller.</summary>
    public const string Cancelled = "cancelled";

    /// <summary>The turn was interrupted by the provider or the host.</summary>
    public const string Interrupted = "interrupted";
}

/// <summary>
/// What caused a run to start.
/// </summary>
/// <remarks>Open vocabulary — an unrecognized value is preserved, not rejected.</remarks>
public static class LifecycleRunCauseKinds
{
    /// <summary>A caller supplied new input.</summary>
    public const string UserInput = "user_input";

    /// <summary>
    /// A delayed tool result resolved and caused a child run. The cause carries the real tool call
    /// id; a synthetic user message is never fabricated. See ADR 0004.
    /// </summary>
    public const string ToolResult = "tool_result";

    /// <summary>A sub-agent run spawned by a parent agent's tool call.</summary>
    public const string SubAgentSpawn = "sub_agent_spawn";
}

/// <summary>
/// Which agent implementation produced an event.
/// </summary>
/// <remarks>
/// Present so consumers can attribute an event, not so they can branch on it: the six V1 events are
/// identical in shape and meaning across every value here. Open vocabulary.
/// </remarks>
public static class LifecycleAgentKinds
{
    /// <summary>The raw multi-turn LLM loop.</summary>
    public const string Raw = "raw";

    /// <summary>The Claude CLI-backed loop.</summary>
    public const string Claude = "claude";

    /// <summary>The Codex CLI-backed loop.</summary>
    public const string Codex = "codex";

    /// <summary>The Copilot CLI-backed loop.</summary>
    public const string Copilot = "copilot";
}

/// <summary>
/// Where a tool call executed.
/// </summary>
/// <remarks>Open vocabulary — an unrecognized value is preserved, not rejected.</remarks>
public static class LifecycleToolKinds
{
    /// <summary>Executed by the host through a registered delegate. Only these can be gated.</summary>
    public const string Host = "host";

    /// <summary>
    /// Executed entirely by the provider or its CLI, never reaching a host delegate. Out of scope
    /// for approval (see ADR 0003).
    /// </summary>
    public const string Provider = "provider";

    /// <summary>A tool call that spawned or addressed a sub-agent.</summary>
    public const string SubAgent = "sub_agent";
}

/// <summary>
/// Terminal outcomes a tool call can report.
/// </summary>
/// <remarks>Open vocabulary — an unrecognized value is preserved, not rejected.</remarks>
public static class LifecycleToolOutcomes
{
    /// <summary>The handler ran and returned a result.</summary>
    public const string Succeeded = "succeeded";

    /// <summary>The handler ran and failed.</summary>
    public const string Failed = "failed";

    /// <summary>
    /// The call was blocked before invocation. The handler did not run. The specific reason is in
    /// <see cref="Payloads.ToolApprovalSummary.Decision"/>.
    /// </summary>
    public const string Denied = "denied";

    /// <summary>The call was cancelled before completing.</summary>
    public const string Cancelled = "cancelled";
}

/// <summary>
/// How complete a usage measurement is.
/// </summary>
/// <remarks>Open vocabulary — an unrecognized value is preserved, not rejected.</remarks>
public static class LifecycleUsageCompleteness
{
    /// <summary>The run had not finished when the measurement was taken.</summary>
    public const string InProgress = "in_progress";

    /// <summary>The run finished but at least one provider response omitted usage.</summary>
    public const string Partial = "partial";

    /// <summary>Every provider response contributing to the run reported usage.</summary>
    public const string Complete = "complete";
}

/// <summary>
/// When rendered context entered a conversation.
/// </summary>
/// <remarks>Open vocabulary — an unrecognized value is preserved, not rejected.</remarks>
public static class LifecycleContextPhases
{
    /// <summary>Discovered and rendered while the agent was starting.</summary>
    public const string Boot = "boot";

    /// <summary>Discovered and rendered after the conversation was already running.</summary>
    public const string MidSession = "mid_session";
}

/// <summary>
/// Whether a reported sandbox inventory can be read as what the session actually loaded.
/// </summary>
/// <remarks>
/// Open vocabulary — an unrecognized value is preserved, not rejected. Only the exact value
/// <see cref="Confirmed"/> means confirmed: anything else, including silence, leaves the inventory
/// unavailable. Requested and merely-available items are never reported as confirmed.
/// </remarks>
public static class LifecycleInventoryStatuses
{
    /// <summary>The gateway confirmed the listed items are loaded in the session.</summary>
    public const string Confirmed = "confirmed";

    /// <summary>
    /// The gateway could not confirm what is loaded. The reason says why, and the item list is
    /// empty.
    /// </summary>
    public const string Unavailable = "unavailable";
}

/// <summary>
/// The kinds of item a confirmed sandbox inventory reports.
/// </summary>
/// <remarks>Open vocabulary — an unrecognized value is preserved, not rejected.</remarks>
public static class LifecycleInventoryKinds
{
    /// <summary>A plugin loaded from a selected marketplace.</summary>
    public const string Plugin = "plugin";

    /// <summary>A skill contributed by a loaded plugin.</summary>
    public const string Skill = "skill";

    /// <summary>A sub-agent contributed by a loaded plugin.</summary>
    public const string Agent = "agent";
}

/// <summary>
/// Capabilities a subscriber may be granted. A capability the host has not granted is absent, and
/// absent means denied.
/// </summary>
public static class LifecycleCapabilities
{
    /// <summary>
    /// Permits delivery of full message and argument content rather than hashes and counts alone.
    /// Without it, content-bearing fields are omitted.
    /// </summary>
    public const string ContentFull = "lifecycle.content.full";

    /// <summary>
    /// Permits the subscriber to answer tool-approval requests. Without it, an approval decision
    /// from that subscriber is rejected.
    /// </summary>
    public const string ToolApprovalDecide = "tool.approval.decide";

    /// <summary>
    /// Every capability this version can grant.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="LifecycleEventTypes.Known"/>, this set is <b>authoritative, not
    /// informational</b>, and the difference is deliberate. An unknown <em>event type</em> is
    /// forward compatibility — a newer producer describing something this version has no typed
    /// payload for — so it is acknowledged and ignored. An unknown <em>capability</em> is the
    /// opposite situation: a subscriber asking for an entitlement this version cannot reason about.
    /// Granting it would record a permission nothing enforces, and the subscriber would believe it
    /// holds access it does not have; ignoring it silently would do the same. Registration
    /// therefore rejects a capability that is not in this set.
    /// <para>
    /// This exists so that callers do not keep private copies of the capability list. A duplicated
    /// set goes stale the day a third capability is added here, and it does so silently — nothing
    /// makes the two disagree loudly, so the copy simply refuses a capability that is now valid.
    /// Read membership from this type.
    /// </para>
    /// </remarks>
    public static IReadOnlySet<string> Known { get; } =
        new HashSet<string>(StringComparer.Ordinal) { ContentFull, ToolApprovalDecide };

    /// <summary>
    /// Indicates whether a capability can be granted by this version.
    /// </summary>
    /// <param name="capability">The capability identifier requested at registration.</param>
    /// <returns>
    /// <see langword="true"/> when the capability is grantable; <see langword="false"/> for
    /// <see langword="null"/>, blank, or unrecognized values, all of which must be refused.
    /// </returns>
    public static bool IsKnown(string? capability) => capability is not null && Known.Contains(capability);
}
