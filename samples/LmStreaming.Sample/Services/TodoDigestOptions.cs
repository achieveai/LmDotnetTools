namespace LmStreaming.Sample.Services;

/// <summary>
///     Configuration for the todo board's change digests (#609). One instance per host, read from
///     the <c>"TodoDigests"</c> configuration section — deliberately SEPARATE from
///     <see cref="TodoNudgeOptions" />: digests are informational fan-out, not budgeted nudges, and
///     the nudge gates (budgets, <c>NudgeRootConversation</c>) must never apply to them.
/// </summary>
/// <remarks>
///     <para>
///         Defaults are ON for both audiences: the primary (root) conversation hears every board
///         change, and each assigned agent hears changes inside its own subtree. Digests are
///         debounced and skipped when a flush finds no net change, so they cannot loop or spam on
///         their own — which is why they can default on where the stall nudges could not.
///     </para>
///     <para>
///         Reading is deliberately tolerant: a missing section, an empty section, or a malformed
///         value reads as the default — never a throw. (Same rationale as
///         <see cref="TodoNudgeOptions" />: the configuration binder enforces nothing and an empty
///         JSON array still creates a section, so hand-rolled <c>TryParse</c> reads are the only
///         shape whose failure mode is guaranteed to be "default".)
///     </para>
/// </remarks>
public sealed record TodoDigestOptions
{
    /// <summary>The configuration section these options are read from.</summary>
    public const string SectionName = "TodoDigests";

    /// <summary>
    ///     Digest of EVERY board change, delivered to the primary (root) conversation. On by
    ///     default — the whole point of #609 is that the root always hears; the nudge-side
    ///     <c>NudgeRootConversation</c> opt-in does not apply here.
    /// </summary>
    public bool PrimaryDigestEnabled { get; init; } = true;

    /// <summary>
    ///     Digest of changes inside an assigned agent's subtree (its assigned tasks and everything
    ///     below them), delivered to that agent's conversation. On by default.
    /// </summary>
    public bool AssigneeDigestEnabled { get; init; } = true;

    /// <summary>Whether the digest service needs to exist at all.</summary>
    public bool AnyDigestEnabled => PrimaryDigestEnabled || AssigneeDigestEnabled;

    /// <summary>
    ///     Reads the <see cref="SectionName" /> section tolerantly. A null configuration, a missing
    ///     or empty section, and malformed values all yield the corresponding defaults — this method
    ///     never throws on configuration content.
    /// </summary>
    public static TodoDigestOptions FromConfiguration(IConfiguration? configuration)
    {
        var defaults = new TodoDigestOptions();
        var section = configuration?.GetSection(SectionName);
        if (section is null)
        {
            return defaults;
        }

        return new TodoDigestOptions
        {
            PrimaryDigestEnabled = ReadBool(section, "PrimaryDigestEnabled", defaults.PrimaryDigestEnabled),
            AssigneeDigestEnabled = ReadBool(section, "AssigneeDigestEnabled", defaults.AssigneeDigestEnabled),
        };
    }

    private static bool ReadBool(IConfiguration section, string key, bool fallback)
    {
        return bool.TryParse(section[key], out var parsed) ? parsed : fallback;
    }
}
