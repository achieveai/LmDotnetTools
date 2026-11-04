using System.Security;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Models;

namespace AchieveAi.LmDotnetTools.LmMultiTurn.ClientTools;

/// <summary>
/// One <c>AskUserQuestion</c> that has just parked, described well enough for a host to show it to
/// the human without reading anybody's transcript.
/// </summary>
/// <param name="RootThreadId">
/// The ROOT conversation the question belongs to — the one a client navigates to in order to answer
/// it. Equal to <paramref name="ThreadId"/> when the root agent itself asked.
/// </param>
/// <param name="ThreadId">The thread of the agent that asked: the root, or a sub-agent transcript.</param>
/// <param name="AgentId">The sub-agent's ordinal id (<c>agent-N</c>), or null when the root asked.</param>
/// <param name="AgentName">The sub-agent's display name when the spawning manager knew one.</param>
/// <param name="ToolCallId">The deferred call's id — the key a settle is matched on.</param>
/// <param name="FunctionArgs">The call's raw JSON arguments, so a host can render the prompt itself.</param>
/// <param name="RaisedAtUtc">When the call deferred.</param>
public sealed record PendingQuestionNotice(
    string RootThreadId,
    string ThreadId,
    string? AgentId,
    string? AgentName,
    string ToolCallId,
    string FunctionArgs,
    DateTimeOffset RaisedAtUtc
);

/// <summary>
/// Told when an <c>AskUserQuestion</c> parks anywhere in a conversation's hierarchy, and when it
/// stops waiting.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is reported and not polled.</b> A parked question leaves no trace a host can notice
/// cheaply. The conversation's <c>lastUpdated</c> stamp moves when a run COMPLETES, and a run that is
/// parked on a question has not completed — so a host watching conversation summaries sees nothing at
/// all until the answer it is waiting for arrives. Every host that wanted to surface a question
/// promptly therefore had to re-read whole transcripts on a timer. This reports the two moments that
/// actually change the answer.
/// </para>
/// <para>
/// <b>Depth.</b> An implementation is handed down from a loop to its <c>SubAgentManager</c> and from
/// there to every child loop, so a question parked at any depth is reported once, by the agent that
/// parked it. Each level wraps what it passes down (see <see cref="ScopedPendingQuestionObserver"/>)
/// so the notice that reaches the host names the ROOT conversation rather than the intermediate one.
/// </para>
/// <para>
/// Implementations must be safe to call from any thread and must NOT block or throw: both members run
/// inline on the loop's own thread, between the deferral being reserved and the run parking. A host
/// that has work to do (a broadcast, a store read) queues it and returns.
/// </para>
/// </remarks>
public interface IPendingQuestionObserver
{
    /// <summary>An <c>AskUserQuestion</c> has parked and is now waiting for the human.</summary>
    /// <param name="notice">What parked, and where.</param>
    /// <remarks>
    /// Raised AFTER the placeholder is in history, so a host that answers this by reading the
    /// transcript finds the call it was told about. It is raised again for the same
    /// <see cref="PendingQuestionNotice.ToolCallId"/> when a restart rebuilds the deferral from
    /// persisted history — a host must treat the pair (root, tool call id) as the identity and
    /// tolerate a repeat rather than counting arrivals.
    /// </remarks>
    void OnQuestionRaised(PendingQuestionNotice notice);

    /// <summary>
    /// A question reported by <see cref="OnQuestionRaised"/> is no longer waiting — answered,
    /// cancelled, or unwound because its placeholder could not be persisted.
    /// </summary>
    /// <param name="rootThreadId">The root conversation named in the raise.</param>
    /// <param name="toolCallId">The call that settled.</param>
    /// <remarks>
    /// <para>
    /// Idempotent by contract: a host may be told a call settled that it never heard raise (a
    /// resolution adopted from persisted history) and must treat that as "not pending" rather than an
    /// error.
    /// </para>
    /// <para>
    /// An early settle — the loop writing the question's placeholder so an interrupting input can run
    /// — is NOT reported here. The human can still answer, so the question is still waiting; this is
    /// called when that answer has been injected into the conversation instead.
    /// </para>
    /// </remarks>
    void OnQuestionSettled(string rootThreadId, string toolCallId);
}

/// <summary>
/// Rewrites the parts of a notice that only an ANCESTOR knows, then forwards it.
/// </summary>
/// <remarks>
/// <para>
/// The root always overwrites: the wrappers nest outward, so the value applied LAST is the one
/// closest to the true root. The name only FILLS, so a grandchild's own name survives the
/// intermediate level that would otherwise stamp its own.
/// </para>
/// This is the whole of how depth is handled. A loop knows its own thread id and nothing about the
/// hierarchy above it, so it reports <c>RootThreadId = ThreadId</c>; each loop that passes the
/// observer down wraps it with its OWN thread id, and a manager wraps it once more per child with the
/// name it just minted. Composing them gives the true root and the asking agent's name without any
/// level having to know the whole chain.
/// </remarks>
public sealed class ScopedPendingQuestionObserver : IPendingQuestionObserver
{
    private readonly IPendingQuestionObserver _inner;
    private readonly string? _rootThreadId;
    private readonly string? _agentName;

    /// <summary>Creates the wrapper.</summary>
    /// <param name="inner">Where the rewritten notice goes.</param>
    /// <param name="rootThreadId">The thread to stamp as the root, or null to leave the reporter's own.</param>
    /// <param name="agentName">A display name to fill in when the notice carries none.</param>
    public ScopedPendingQuestionObserver(
        IPendingQuestionObserver inner,
        string? rootThreadId = null,
        string? agentName = null
    )
    {
        ArgumentNullException.ThrowIfNull(inner);

        _inner = inner;
        _rootThreadId = rootThreadId;
        _agentName = agentName;
    }

    /// <inheritdoc />
    public void OnQuestionRaised(PendingQuestionNotice notice)
    {
        ArgumentNullException.ThrowIfNull(notice);

        _inner.OnQuestionRaised(
            notice with
            {
                RootThreadId = _rootThreadId ?? notice.RootThreadId,
                AgentName = notice.AgentName ?? _agentName,
            }
        );
    }

    /// <inheritdoc />
    public void OnQuestionSettled(string rootThreadId, string toolCallId) =>
        _inner.OnQuestionSettled(_rootThreadId ?? rootThreadId, toolCallId);
}

/// <summary>
/// Builds the notice for a deferred call, or returns null when the call is not a question.
/// </summary>
/// <remarks>
/// Kept beside the contract rather than in the loop so the "is this an AskUserQuestion" test and the
/// agent-id derivation have one definition. <see cref="PendingQuestionNotice.AgentId"/> comes from
/// the reporter's own thread id, which carries it at every depth since #705.
/// </remarks>
public static class PendingQuestionNotices
{
    /// <summary>
    /// Describes <paramref name="toolCallId"/> as a pending question, or returns null when
    /// <paramref name="functionName"/> is some other deferring tool.
    /// </summary>
    /// <param name="threadId">The reporting agent's own thread.</param>
    /// <param name="functionName">The tool that deferred.</param>
    /// <param name="toolCallId">The deferred call.</param>
    /// <param name="functionArgs">The call's JSON arguments.</param>
    /// <param name="raisedAtUtc">When it deferred.</param>
    public static PendingQuestionNotice? TryDescribe(
        string threadId,
        string? functionName,
        string toolCallId,
        string? functionArgs,
        DateTimeOffset raisedAtUtc
    ) =>
        string.Equals(functionName, AskUserQuestionToolProvider.ToolName, StringComparison.Ordinal)
            ? new PendingQuestionNotice(
                RootThreadId: threadId,
                ThreadId: threadId,
                AgentId: SubAgentThreadIds.TryGetAgentId(threadId, out var agentId) ? agentId : null,
                AgentName: null,
                ToolCallId: toolCallId,
                FunctionArgs: functionArgs ?? "{}",
                RaisedAtUtc: raisedAtUtc
            )
            : null;

    /// <summary>
    /// Whether the <c>AskUserQuestion</c> call <paramref name="toolCallId"/> is still waiting for the
    /// human, judged from <paramref name="history"/> alone.
    /// </summary>
    /// <param name="history">The asking agent's own transcript.</param>
    /// <param name="toolCallId">The question's call id.</param>
    /// <remarks>
    /// <para>
    /// The server-side twin of the client's <c>isQuestionAwaitingAnswer</c>, for a host that has to
    /// decide without a live loop — re-checking, after a restart, the questions it was showing before.
    /// </para>
    /// <para>
    /// Two endings leave a question open. The ordinary one: its result is still the deferred
    /// placeholder. The other: the loop settled it early so an interrupting input could run, and the
    /// human's real answer — which arrives later as a user turn under the injection tag, naming this
    /// call — has not landed yet. False when the call has no result in this transcript at all.
    /// </para>
    /// </remarks>
    public static bool IsAwaitingAnswer(IEnumerable<IMessage> history, string toolCallId)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentException.ThrowIfNullOrEmpty(toolCallId);

        var tag = EarlySettlePlaceholders.TryGet(AskUserQuestionToolProvider.ToolName, out var spec)
            ? spec.InjectionTag
            : "user-answer";
        var envelopeStart = $"<{tag} ";
        var namesThisCall = $" tool-call-id=\"{SecurityElement.Escape(toolCallId)}\"";

        ToolCallResultMessage? result = null;
        var answered = false;
        foreach (var message in history)
        {
            if (message is ToolCallResultMessage candidate)
            {
                if (string.Equals(candidate.ToolCallId, toolCallId, StringComparison.Ordinal))
                {
                    result = candidate;
                }
            }
            else if (
                message is TextMessage { Role: Role.User } text
                && text.Text.StartsWith(envelopeStart, StringComparison.Ordinal)
            )
            {
                // The header line only: the request and answer bodies are inlined verbatim below it
                // and may quote another call's envelope.
                var headerEnd = text.Text.IndexOf('\n', StringComparison.Ordinal);
                var header = headerEnd < 0 ? text.Text : text.Text[..headerEnd];
                answered |= header.Contains(namesThisCall, StringComparison.Ordinal);
            }
        }

        return result is not null
            && (result.IsDeferred || (EarlySettlePlaceholders.IsEarlySettleResult(result.Result) && !answered));
    }
}
