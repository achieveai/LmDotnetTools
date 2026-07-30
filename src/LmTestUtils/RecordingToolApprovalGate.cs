using AchieveAi.LmDotnetTools.LmCore.Approval;

namespace AchieveAi.LmDotnetTools.LmTestUtils;

/// <summary>
/// An <see cref="IToolApprovalGate"/> that answers from a script and keeps every request it was
/// asked, so a test can assert both what a gate decided and — often the sharper question — whether
/// it was consulted at all.
/// </summary>
/// <remarks>
/// <para>
/// "Was the approver ever asked?" is the whole assertion for the paths that must NOT be gated (a
/// workflow controller's own orchestration steps) and for the paths that must be gated before they
/// run. A mocking framework could express it, but the recorded <see cref="ToolApprovalContext"/>
/// list is what makes the follow-up checks — which tool, which arguments, which frozen hash —
/// readable.
/// </para>
/// <para>
/// Lives here rather than in one test project because more than one assembly needs it, and a
/// second copy would be a second place for the capture to be subtly wrong.
/// </para>
/// </remarks>
public sealed class RecordingToolApprovalGate : IToolApprovalGate
{
    private readonly Func<ToolApprovalContext, ToolApprovalVerdict> _decide;
    private readonly List<ToolApprovalContext> _requests = [];
    private readonly object _sync = new();

    /// <summary>
    /// Creates a gate that decides with <paramref name="decide"/>.
    /// </summary>
    /// <param name="decide">
    /// The verdict for each request. Null allows everything — the right default for a gate whose
    /// point is to observe that it was reached.
    /// </param>
    public RecordingToolApprovalGate(Func<ToolApprovalContext, ToolApprovalVerdict>? decide = null) =>
        _decide = decide ?? (_ => ToolApprovalVerdict.Allow());

    /// <summary>A gate that allows every call, recording each one.</summary>
    public static RecordingToolApprovalGate Allowing() => new();

    /// <summary>A gate that refuses every call.</summary>
    /// <param name="reason">Optional detail carried into the refusal message.</param>
    public static RecordingToolApprovalGate Denying(string? reason = null) =>
        new(_ => ToolApprovalVerdict.Deny(reason));

    /// <summary>Every request this gate was asked, in the order it saw them.</summary>
    public IReadOnlyList<ToolApprovalContext> Requests
    {
        get
        {
            lock (_sync)
            {
                return [.. _requests];
            }
        }
    }

    /// <summary>Whether the gate was consulted even once.</summary>
    public bool WasConsulted => Requests.Count > 0;

    /// <summary>The tool names this gate was asked about, in order.</summary>
    public IReadOnlyList<string> ToolNames => [.. Requests.Select(r => r.ToolName)];

    /// <inheritdoc />
    public ValueTask<ToolApprovalVerdict> RequestApprovalAsync(
        ToolApprovalContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_sync)
        {
            _requests.Add(context);
        }

        return ValueTask.FromResult(_decide(context));
    }
}
