namespace AchieveAi.LmDotnetTools.LmMultiTurn.Triggers;

/// <summary>
/// Host-side registration of a trigger kind: the kind name the LLM uses, human-readable docs and
/// an args-schema hint surfaced in the <c>Wait</c> tool description, the source's capabilities,
/// and the (stateless, reusable) source instance that arms waits of this kind.
/// </summary>
public sealed record TriggerSourceRegistration
{
    /// <summary>The kind token the LLM passes as <c>Wait({kind})</c>. Unique per runtime.</summary>
    public required string Kind { get; init; }

    /// <summary>One-line description of what this kind waits for, shown in the tool contract.</summary>
    public required string Description { get; init; }

    /// <summary>
    /// Human-readable hint describing the <c>args</c> object for this kind (e.g.
    /// <c>{ delay?: "10m", deadline?: "2026-07-31T12:00:00Z" }</c>). Shown in the tool contract.
    /// </summary>
    public required string ArgsSchema { get; init; }

    /// <summary>What this source supports (block / notify / restore).</summary>
    public required TriggerCapabilities Capabilities { get; init; }

    /// <summary>The stateless source instance that arms waits of this kind.</summary>
    public required ITriggerSource Source { get; init; }
}
