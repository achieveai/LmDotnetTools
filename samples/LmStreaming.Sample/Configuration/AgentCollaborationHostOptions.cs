using AchieveAi.LmDotnetTools.LmMultiTurn.Collaboration;

namespace LmStreaming.Sample.Configuration;

/// <summary>
/// Host-facing configuration for hierarchy-wide agent collaboration (#244), bound from the
/// <c>AgentCollaboration</c> section.
/// </summary>
/// <remarks>
/// <para>
/// Collaboration is <em>opt-in</em>. In the library the feature gate is the absence of an
/// <see cref="AgentCollaborationOptions"/> object; configuration files cannot express "absent"
/// as cleanly as they express "present but false", so this host shape adds an explicit
/// <see cref="Enabled"/> flag and only materialises the library options when it is set. A sample
/// deployment that never adds the section — or adds it with <c>Enabled: false</c> — keeps today's
/// behaviour byte-for-byte: legacy tool schemas, one level of ordinary nesting, per-manager limits
/// only, and no collaboration state written anywhere.
/// </para>
/// <para>
/// Follows the same idiom as <c>ContextDiscoveryOptions</c> / <c>SandboxGatewayOptions</c>: a
/// <c>sealed class</c> with mutable properties so the configuration binder can populate it.
/// Retention is expressed in minutes rather than as a <see cref="TimeSpan"/> because a plain number
/// round-trips through JSON configuration and environment variables without the binder's
/// culture-sensitive timespan parsing.
/// </para>
/// </remarks>
public sealed class AgentCollaborationHostOptions
{
    /// <summary>Configuration section these options are bound from.</summary>
    public const string SectionName = "AgentCollaboration";

    /// <summary>
    /// Whether to enable hierarchy-wide collaboration. Default <c>false</c>, which reproduces the
    /// pre-#244 surface exactly.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>Deepest ordinary delegation hop allowed; the root sits at delegation depth 0.</summary>
    public int MaxDelegationDepth { get; set; } = 1;

    /// <summary>Root-wide ceiling on simultaneously admitted agents across every nested manager.</summary>
    public int MaxTotalAgents { get; set; } = 32;

    /// <summary>Largest number of undelivered messages one target may hold.</summary>
    public int MaxInboxMessages { get; set; } = 32;

    /// <summary>How long a closed message-ledger entry is retained for idempotency, in minutes.</summary>
    public int ClosedEntryRetentionMinutes { get; set; } = 30;

    /// <summary>Hard cap on retained closed ledger entries.</summary>
    public int MaxClosedEntries { get; set; } = 1024;

    /// <summary>
    /// Who may read another agent's transcript: <c>Ancestors</c> (default, narrowest) or <c>Open</c>.
    /// </summary>
    public string TranscriptVisibility { get; set; } = nameof(TranscriptVisibilityMode.Ancestors);

    /// <summary>
    /// Materialises the validated library options, or null when collaboration is switched off.
    /// </summary>
    /// <remarks>
    /// Validation runs here — at startup, from the composition root — rather than at the first spawn,
    /// so a typo in <c>appsettings.json</c> fails the boot it belongs to instead of surfacing as a
    /// confusing mid-conversation tool error.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// <see cref="TranscriptVisibility"/> is not a defined mode.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">A limit or retention value is unusable.</exception>
    public AgentCollaborationOptions? ToCollaborationOptions()
    {
        if (!Enabled)
        {
            return null;
        }

        if (!Enum.TryParse<TranscriptVisibilityMode>(TranscriptVisibility, ignoreCase: true, out var mode)
            || !Enum.IsDefined(mode))
        {
            throw new InvalidOperationException(
                $"{SectionName}:{nameof(TranscriptVisibility)} must be one of "
                + string.Join(", ", Enum.GetNames<TranscriptVisibilityMode>())
                + $"; got '{TranscriptVisibility}'.");
        }

        // Retention is converted before Validate() so a non-positive configured value is reported by
        // the library's own guard rather than silently becoming a zero-length window.
        var options = new AgentCollaborationOptions
        {
            MaxDelegationDepth = MaxDelegationDepth,
            MaxTotalAgents = MaxTotalAgents,
            MaxInboxMessages = MaxInboxMessages,
            ClosedEntryRetention = TimeSpan.FromMinutes(ClosedEntryRetentionMinutes),
            MaxClosedEntries = MaxClosedEntries,
            TranscriptVisibility = mode,
        };

        options.Validate();
        return options;
    }
}
