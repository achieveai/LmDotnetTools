using CodeReviewDaemon.Sample.Persistence;

namespace CodeReviewDaemon.Sample.Workspace;

/// <summary>
/// The production <see cref="IPolicyRefusalRecorder"/>: writes every refusal to the daemon's own store
/// (<c>policy_refusal</c>, migration v6) and mirrors it to the log at Warning so it is visible in both the
/// place an operator queries and the place an operator tails.
/// <para>
/// <b>Never throws.</b> A store that is locked, full, or mid-migration must not be able to convert a
/// refusal into an exception at the enforcement site: the write is already denied by the time this is
/// called, and letting the RECORDING fail the call would turn an audit gap into an outage. A failed write
/// is itself logged, so the gap is visible rather than silent.
/// </para>
/// </summary>
internal sealed class StorePolicyRefusalRecorder : IPolicyRefusalRecorder
{
    private readonly ReviewStore _store;
    private readonly ILogger<StorePolicyRefusalRecorder> _logger;

    public StorePolicyRefusalRecorder(ReviewStore store, ILogger<StorePolicyRefusalRecorder> logger)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public void Record(PolicyRefusalRecord refusal)
    {
        ArgumentNullException.ThrowIfNull(refusal);

        _logger.LogWarning(
            "Policy refusal recorded: kind={RefusalKind} provider={RefusalProvider} subject={RefusalSubject} "
                + "method={RefusalMethod} target={RefusalTarget} reason={RefusalReason}",
            refusal.Kind,
            refusal.Provider,
            refusal.Subject,
            refusal.Method,
            refusal.Target,
            refusal.Reason
        );

        try
        {
            _store.RecordPolicyRefusal(refusal);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Could not persist the {RefusalKind} refusal of {RefusalSubject}; the refusal HELD, but it is "
                    + "not in the ledger and only this line records it.",
                refusal.Kind,
                refusal.Subject
            );
        }
    }
}
