namespace AchieveAi.LmDotnetTools.LmMultiTurn.Collaboration;

/// <summary>
/// Who may read another agent's transcript within one collaboration.
/// </summary>
/// <remarks>
/// Directory visibility and contact permission are deliberately not the same thing as transcript
/// visibility: every member of a collaboration can address every other member, while this mode
/// decides who may <em>read</em> one. The mode is trusted root configuration captured once at
/// <see cref="AgentCollaborationBundle"/> construction; no model argument can change it later.
/// </remarks>
public enum TranscriptVisibilityMode
{
    /// <summary>
    /// Only an agent's ancestors may read its transcript. The narrowest mode, and the default.
    /// </summary>
    Ancestors,

    /// <summary>
    /// Every agent in the collaboration may read every other one. An explicit trust-mode choice
    /// that is never enabled implicitly.
    /// </summary>
    Open,
}

/// <summary>
/// Root configuration that turns hierarchy-wide agent collaboration on and bounds it.
/// </summary>
/// <remarks>
/// <para>
/// The <em>absence</em> of this object is the feature gate. A host that never supplies one keeps
/// today's behaviour exactly: legacy tool schemas, one level of ordinary nesting, per-manager
/// limits only, and no collaboration state. Supplying one — even with every value left at its
/// default — opts the whole root hierarchy in.
/// </para>
/// <para>
/// Because of that, every default here is chosen to reproduce current behaviour rather than to be
/// generous: <see cref="MaxDelegationDepth"/> is 1, which is the single ordinary hop that exists
/// today, and <see cref="TranscriptVisibility"/> is the narrowest mode.
/// </para>
/// </remarks>
public sealed record AgentCollaborationOptions
{
    /// <summary>
    /// Deepest ordinary delegation hop allowed, where the root sits at delegation depth 0.
    /// </summary>
    /// <remarks>
    /// The default of 1 permits exactly one level of ordinary sub-agents, which is what the
    /// runtime does today. A value of 0 admits a collaboration in which nothing may spawn.
    /// </remarks>
    public int MaxDelegationDepth { get; init; } = 1;

    /// <summary>
    /// Root-wide ceiling on simultaneously admitted agents across every nested manager.
    /// </summary>
    /// <remarks>
    /// This is the breadth bound that per-manager concurrency limits cannot provide, because each
    /// nested parent owns an independent manager and gate. An agent counts against it from the
    /// moment its spawn is accepted — including while merely queued — so queued work cannot
    /// overshoot the cap.
    /// </remarks>
    public int MaxTotalAgents { get; init; } = 32;

    /// <summary>
    /// Largest number of undelivered messages one target may hold.
    /// </summary>
    /// <remarks>
    /// The bound is per target in total, not per sender. There is no per-sender fairness quota in
    /// v1: one sender can fill a target's inbox, after which every sender receives an explicit
    /// recoverable backpressure result. Messages are never silently dropped.
    /// </remarks>
    public int MaxInboxMessages { get; init; } = 32;

    /// <summary>
    /// How long a closed message-ledger entry is retained for idempotency after it closes.
    /// </summary>
    /// <remarks>
    /// Open entries are never evicted; only closed and terminally failed ones age out. Retention
    /// is measured against the ledger's injected <see cref="TimeProvider"/> so the window is
    /// testable without sleeping.
    /// </remarks>
    public TimeSpan ClosedEntryRetention { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Hard cap on retained closed ledger entries, evicting oldest-closed first.
    /// </summary>
    /// <remarks>
    /// This is the backstop for a collaboration that closes messages faster than
    /// <see cref="ClosedEntryRetention"/> can age them out, so ledger memory stays bounded
    /// regardless of traffic shape.
    /// </remarks>
    public int MaxClosedEntries { get; init; } = 1024;

    /// <summary>Who may read another agent's transcript. Defaults to the narrowest mode.</summary>
    public TranscriptVisibilityMode TranscriptVisibility { get; init; } = TranscriptVisibilityMode.Ancestors;

    /// <summary>
    /// Throws when any limit is unusable, so a misconfigured host fails at construction rather
    /// than at the first send.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">A limit, retention, or mode is invalid.</exception>
    public void Validate()
    {
        if (MaxDelegationDepth < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxDelegationDepth),
                MaxDelegationDepth,
                "Maximum delegation depth cannot be negative; the root is depth 0."
            );
        }

        if (MaxTotalAgents < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxTotalAgents),
                MaxTotalAgents,
                "A collaboration must admit at least one agent."
            );
        }

        if (MaxInboxMessages < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxInboxMessages),
                MaxInboxMessages,
                "A target inbox must hold at least one message."
            );
        }

        if (ClosedEntryRetention <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ClosedEntryRetention),
                ClosedEntryRetention,
                "Closed-entry retention must be positive; zero would defeat idempotency."
            );
        }

        if (MaxClosedEntries < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxClosedEntries),
                MaxClosedEntries,
                "At least one closed entry must be retained for idempotency."
            );
        }

        if (!Enum.IsDefined(TranscriptVisibility))
        {
            throw new ArgumentOutOfRangeException(
                nameof(TranscriptVisibility),
                TranscriptVisibility,
                "Transcript visibility must be a defined mode."
            );
        }
    }
}
