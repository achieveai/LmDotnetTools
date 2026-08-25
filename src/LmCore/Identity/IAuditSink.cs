namespace AchieveAi.LmDotnetTools.LmCore.Identity;

/// <summary>How an audit record should be triaged.</summary>
public enum AuditEventClass
{
    /// <summary>Expected traffic. Retained for reconstruction, not for alerting.</summary>
    Routine = 0,

    /// <summary>Security-relevant: a rejection, a cross-tenant attempt, or an operator action.</summary>
    Security = 1,
}

/// <summary>Which front door produced an authentication record.</summary>
public enum AuditFrontDoor
{
    /// <summary>Interactive Entra sign-in from the SPA (spec 4.1).</summary>
    Interactive = 0,

    /// <summary>App credential plus host-asserted on-behalf-of JWT (spec 4.2).</summary>
    S2SObo = 1,

    /// <summary>Host-minted embed token (spec 6).</summary>
    Embed = 2,
}

/// <summary>Whether an authentication attempt produced a principal.</summary>
public enum AuthenticationOutcome
{
    /// <summary>A principal was constructed.</summary>
    Accepted = 0,

    /// <summary>No principal was constructed; the record's reason says why.</summary>
    Rejected = 1,
}

/// <summary>Whether an access decision permitted the action.</summary>
public enum AuthorizationOutcome
{
    /// <summary>The action was permitted.</summary>
    Allow = 0,

    /// <summary>The action was refused.</summary>
    Deny = 1,
}

/// <summary>What an operator-console operation did.</summary>
public enum AdministrationOutcome
{
    /// <summary>The operation changed data.</summary>
    Applied = 0,

    /// <summary>A rehearsal: the operation reported what it would change and changed nothing.</summary>
    Rehearsed = 1,

    /// <summary>The operation was refused; the record's reason says why.</summary>
    Rejected = 2,
}

/// <summary>
/// Fields common to every audit record kind, so one query over the <c>Audit</c> source context
/// returns all of them and <c>recordKind</c> discriminates.
/// </summary>
/// <remarks>
/// <c>eventId</c> and <c>timestamp</c> are deliberately not members: <see cref="IAuditSink"/>
/// stamps them, so no call site can forget one or invent its own clock. That is also what makes
/// the later migration to P4's durable outbox mechanical - the outbox implementation stamps them
/// the same way and nothing else changes.
/// </remarks>
public abstract record AuditRecord
{
    /// <summary>Constant discriminator for this record kind.</summary>
    public abstract string RecordKind { get; }

    /// <summary>Ambient request or run correlation id.</summary>
    public string? CorrelationId { get; init; }

    /// <summary>How this record should be triaged.</summary>
    public required AuditEventClass EventClass { get; init; }

    /// <summary>Stable failure or outcome code. Null when a successful outcome needs no code.</summary>
    public string? Reason { get; init; }
}

/// <summary>
/// A pre-principal record, written by the authentication handlers. The events that most need an
/// audit trail - an unprovisioned tenant, a cross-tenant token, a replayed <c>jti</c> - all occur
/// before a <see cref="Principal"/> exists, because they are the reasons none was constructed. So
/// every identity field here is what the presented token CLAIMED, not what was resolved.
/// </summary>
/// <remarks>
/// No token, no signature and no bearer value is ever carried here.
/// </remarks>
public sealed record AuthenticationAuditRecord : AuditRecord
{
    /// <inheritdoc />
    public override string RecordKind => "authentication";

    /// <summary>Which front door was being used.</summary>
    public required AuditFrontDoor FrontDoor { get; init; }

    /// <summary>The raw <c>tid</c> claim, or null if the token did not parse.</summary>
    public string? ClaimedEntraTenantId { get; init; }

    /// <summary>The raw <c>oid</c> claim, or null.</summary>
    public string? ClaimedObjectId { get; init; }

    /// <summary>
    /// The <c>preferred_username</c> claim, populated only when <c>Identity:Audit:IncludeUpn</c>
    /// is set. A rejected sign-in is exactly the case where the presented identifier belongs to
    /// someone who is not our user, so some deployments will not want it retained at all.
    /// </summary>
    public string? ClaimedUpn { get; init; }

    /// <summary>The presented <c>X-Sbx-App-Id</c>, or null.</summary>
    public string? AppId { get; init; }

    /// <summary>Our internal <c>tnt_*</c> id if resolution succeeded, else null.</summary>
    public string? ResolvedTenantId { get; init; }

    /// <summary>The token's <c>jti</c>, for correlating a replay with its original use.</summary>
    public string? Jti { get; init; }

    /// <summary>Whether a principal was constructed.</summary>
    public required AuthenticationOutcome Outcome { get; init; }
}

/// <summary>
/// A post-principal record, written by <see cref="IResourceAccessPolicy"/> and by the admin
/// listing path, where a <see cref="Principal"/> exists by construction. Both allows and denies
/// are recorded: a deny-only trail cannot answer "was this ever attempted successfully?".
/// </summary>
public sealed record AuthorizationAuditRecord : AuditRecord
{
    /// <inheritdoc />
    public override string RecordKind => "authorization";

    /// <summary>The party that made the call.</summary>
    public required PrincipalRef Actor { get; init; }

    /// <summary>The party the actor acted for, null when absent.</summary>
    public PrincipalRef? OnBehalfOf { get; init; }

    /// <summary>Our internal tenant id.</summary>
    public required string TenantId { get; init; }

    /// <summary>App id from the app credential, when one authenticated the call.</summary>
    public string? AppId { get; init; }

    /// <summary>Which front door authenticated the principal.</summary>
    public required PrincipalSource Source { get; init; }

    /// <summary>The action being decided.</summary>
    public required AccessAction Permission { get; init; }

    /// <summary>The target resource; its id is <c>*</c> for a listing decision.</summary>
    public required ResourceRef Resource { get; init; }

    /// <summary>Whether the action was permitted.</summary>
    public required AuthorizationOutcome Outcome { get; init; }
}

/// <summary>
/// An operator-console record, written by the paths that act on the operator trust boundary.
/// These authenticate with a shared operator secret, so they have no <see cref="Principal"/> and
/// what they record is not an access decision.
/// </summary>
public sealed record AdministrationAuditRecord : AuditRecord
{
    /// <inheritdoc />
    public override string RecordKind => "administration";

    /// <summary>Which operation, e.g. <c>provision_tenant</c>.</summary>
    public required string Operation { get; init; }

    /// <summary>
    /// Always <c>s2s_operator_secret</c>. A constant rather than an identity, and that is the
    /// honest limitation: the shared secret attests that the operator credential was presented,
    /// and from where, but not by whom. Per-operator attribution needs an operator directory,
    /// which P1 does not build.
    /// </summary>
    public string OperatorAuth => "s2s_operator_secret";

    /// <summary>The caller's address - the only distinguishing fact available.</summary>
    public string? RemoteAddress { get; init; }

    /// <summary>The tenant the operation targeted.</summary>
    public string? TargetTenantId { get; init; }

    /// <summary>The <c>ownerUserId</c> body field, or null.</summary>
    public string? TargetOwnerUserId { get; init; }

    /// <summary>Rows the call did change, or would have changed under <see cref="DryRun"/>.</summary>
    public int AffectedCount { get; init; }

    /// <summary>Whether the call was a rehearsal.</summary>
    public bool DryRun { get; init; }

    /// <summary>What the operation did.</summary>
    public required AdministrationOutcome Outcome { get; init; }
}

/// <summary>
/// The single seam every audit record goes through. Deliberately three overloads rather than one
/// record type: the three kinds have genuinely disjoint identity fields, and a single type whose
/// fields are sourced from <see cref="Principal"/> could not carry the pre-principal rejections
/// that most need auditing.
/// </summary>
/// <remarks>
/// No record's field set may be extended or trimmed at a call site. P1 implements this over the
/// existing structured logs; migrating to P4's durable outbox means reimplementing this interface
/// and changing nothing else.
/// </remarks>
public interface IAuditSink
{
    /// <summary>Writes a pre-principal authentication record.</summary>
    /// <param name="record">The record. Its event id and timestamp are stamped by the sink.</param>
    void Write(AuthenticationAuditRecord record);

    /// <summary>Writes a post-principal authorization record.</summary>
    /// <param name="record">The record. Its event id and timestamp are stamped by the sink.</param>
    void Write(AuthorizationAuditRecord record);

    /// <summary>Writes an operator-console record.</summary>
    /// <param name="record">The record. Its event id and timestamp are stamped by the sink.</param>
    void Write(AdministrationAuditRecord record);
}
