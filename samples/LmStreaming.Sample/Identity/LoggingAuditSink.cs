using AchieveAi.LmDotnetTools.LmCore.Identity;

namespace LmStreaming.Sample.Identity;

/// <summary>
/// Writes audit records to the existing structured logs, under a fixed <c>Audit</c> source
/// context, so one DuckDB query over <c>SourceContext = 'Audit'</c> returns all three kinds and
/// <c>recordKind</c> discriminates them.
/// </summary>
/// <remarks>
/// <para>
/// Interim by design. #237 routes audit through P4's durable outbox; P4 does not exist, and
/// blocking every authorization decision in P1 on a pillar that has not started would be the wrong
/// trade. Migrating means reimplementing <see cref="IAuditSink"/> against the outbox and changing
/// nothing else - which only holds because every record goes through this one sink rather than
/// through ad-hoc logging at call sites.
/// </para>
/// <para>
/// THE HONEST LIMITATION: log retention is an operational setting chosen for debugging, not an
/// audit-retention guarantee. Until the outbox lands this is a diagnostic-grade trail and must not
/// be described to a customer as a compliance-grade one.
/// </para>
/// </remarks>
public sealed class LoggingAuditSink : IAuditSink
{
    /// <summary>Logger category every audit record is written under.</summary>
    public const string SourceContext = "Audit";

    private readonly ILogger _logger;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates a sink writing through the given logger factory.</summary>
    /// <param name="loggerFactory">Factory used to create the fixed <c>Audit</c> category logger.</param>
    /// <param name="timeProvider">Clock stamped onto every record.</param>
    public LoggingAuditSink(ILoggerFactory loggerFactory, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _logger = loggerFactory.CreateLogger(SourceContext);
        _timeProvider = timeProvider;
    }

    /// <inheritdoc />
    public void Write(AuthenticationAuditRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        _logger.Log(
            LevelFor(record.EventClass),
            "Audit {RecordKind} {EventId} {Timestamp} {FrontDoor} {Outcome} {Reason} "
                + "{ClaimedEntraTenantId} {ClaimedObjectId} {ClaimedUpn} {AppId} {ResolvedTenantId} "
                + "{Jti} {CorrelationId} {EventClass}",
            record.RecordKind,
            Guid.NewGuid(),
            _timeProvider.GetUtcNow(),
            record.FrontDoor,
            record.Outcome,
            record.Reason,
            record.ClaimedEntraTenantId,
            record.ClaimedObjectId,
            record.ClaimedUpn,
            record.AppId,
            record.ResolvedTenantId,
            record.Jti,
            record.CorrelationId,
            record.EventClass
        );
    }

    /// <inheritdoc />
    public void Write(AuthorizationAuditRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        _logger.Log(
            LevelFor(record.EventClass),
            "Audit {RecordKind} {EventId} {Timestamp} {ActorKind} {ActorId} {OnBehalfOfKind} "
                + "{OnBehalfOfId} {TenantId} {AppId} {Source} {Permission} {ResourceType} "
                + "{ResourceId} {Outcome} {Reason} {CorrelationId} {EventClass}",
            record.RecordKind,
            Guid.NewGuid(),
            _timeProvider.GetUtcNow(),
            record.Actor.Kind,
            record.Actor.Id,
            record.OnBehalfOf?.Kind,
            record.OnBehalfOf?.Id,
            record.TenantId,
            record.AppId,
            record.Source,
            record.Permission,
            record.Resource.Type,
            record.Resource.Id,
            record.Outcome,
            record.Reason,
            record.CorrelationId,
            record.EventClass
        );
    }

    /// <inheritdoc />
    public void Write(AdministrationAuditRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        _logger.Log(
            LevelFor(record.EventClass),
            "Audit {RecordKind} {EventId} {Timestamp} {Operation} {OperatorAuth} {RemoteAddress} "
                + "{TargetTenantId} {TargetOwnerUserId} {AffectedCount} {DryRun} {Outcome} "
                + "{Reason} {CorrelationId} {EventClass}",
            record.RecordKind,
            Guid.NewGuid(),
            _timeProvider.GetUtcNow(),
            record.Operation,
            record.OperatorAuth,
            record.RemoteAddress,
            record.TargetTenantId,
            record.TargetOwnerUserId,
            record.AffectedCount,
            record.DryRun,
            record.Outcome,
            record.Reason,
            record.CorrelationId,
            record.EventClass
        );
    }

    /// <summary>
    /// Security-class records are Warning so they surface in a default-level deployment; routine
    /// ones are Information. The level is derived from the record rather than chosen per call
    /// site, because a call site free to pick its own level is a call site that can hide a
    /// rejection.
    /// </summary>
    private static LogLevel LevelFor(AuditEventClass eventClass) =>
        eventClass == AuditEventClass.Security ? LogLevel.Warning : LogLevel.Information;
}
