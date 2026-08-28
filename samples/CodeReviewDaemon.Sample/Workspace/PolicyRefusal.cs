namespace CodeReviewDaemon.Sample.Workspace;

/// <summary>What the daemon refused. The kinds are deliberately coarse — this is an audit trail, not a
/// taxonomy — but a refused WRITE and a refused read must never collapse into one bucket, because only the
/// first answers "did the collect-only posture hold this run?".</summary>
internal enum PolicyRefusalKind
{
    /// <summary>A provider-API request whose method mutates state (POST/PUT/PATCH/DELETE) was denied.</summary>
    ProviderWrite,

    /// <summary>A non-mutating request was denied (wrong host, off-route, unclassified, …).</summary>
    ProviderRead,

    /// <summary>A sub-agent template the run had no capability to run was refused.</summary>
    SubAgentSpawn,
}

/// <summary>
/// One refusal, in a shape that answers the question an operator actually asks afterwards: <i>what did this
/// daemon stop, when, and why</i>.
/// <para>
/// This exists because the evidence that "collect-only was honoured" was, until now, the absence of a
/// <c>Posted</c> row in <c>review_outbox</c> — and a sub-agent posting straight to the provider REST API
/// never touches that table, so the only recorded signal was structurally blind to the one event class it
/// was being read as proof against. A refusal that leaves no trace is indistinguishable from a refusal that
/// never had to happen.
/// </para>
/// </summary>
/// <param name="AtUtc">When the refusal was made.</param>
/// <param name="Kind">Which class of capability was withheld.</param>
/// <param name="Provider">Provider key (<c>github</c>/<c>ado</c>), or <c>daemon</c> for non-provider refusals.</param>
/// <param name="Subject">
/// What was refused: the <see cref="SandboxOperation"/> name for an egress denial, the sub-agent template
/// name for a spawn denial.
/// </param>
/// <param name="Method">The HTTP method, or <c>spawn</c> for a spawn refusal.</param>
/// <param name="Target">The URI/path, or the run+thread a spawn was observed on.</param>
/// <param name="Reason">The policy's audit-grade rationale, verbatim.</param>
internal sealed record PolicyRefusalRecord(
    DateTimeOffset AtUtc,
    PolicyRefusalKind Kind,
    string Provider,
    string Subject,
    string Method,
    string Target,
    string Reason
);

/// <summary>
/// Where refusals are recorded. Separated from the enforcement sites so neither the HTTP handler nor the
/// spawn gate has to know whether the sink is a database, a log, or a test double — and so a daemon wired
/// without a sink still ENFORCES (the gate never depends on the recorder being present).
/// </summary>
internal interface IPolicyRefusalRecorder
{
    /// <summary>
    /// Records <paramref name="refusal"/>. Implementations must not throw: a refusal that fails to persist
    /// must still have been a refusal, so the recording is best-effort by contract and the caller never
    /// wraps it.
    /// </summary>
    void Record(PolicyRefusalRecord refusal);
}
