using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Identity;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using LmStreaming.Sample.Persistence;

namespace LmStreaming.Sample.Identity;

/// <summary>
/// The outcome of gating one <c>/ws/subagent</c> handshake.
/// </summary>
/// <param name="Admitted">
/// Whether the handshake may be accepted at all. False means a refusal has already been written to
/// the response and the socket must not be accepted.
/// </param>
/// <param name="MayReplayPersistedTranscript">
/// Whether the handler may fall back to the child's PERSISTED transcript when no live stream
/// resolves. False for a child whose durable parent link names a different conversation: the caller
/// is entitled to the parent they named, but not to that child, and the socket must answer exactly as
/// it answers for an agent id that names nothing at all. Meaningless when
/// <paramref name="Admitted"/> is false.
/// </param>
public sealed record SubAgentSocketAdmission(bool Admitted, bool MayReplayPersistedTranscript);

/// <summary>
/// The per-conversation authorization the WebSocket transports were missing (#419): the socket
/// sibling of <c>ConversationsController</c>'s <c>Authorize</c>/<c>Refuse</c> pair, run BEFORE the
/// handshake is accepted.
/// </summary>
/// <remarks>
/// <para>
/// Before this existed, <c>/ws</c> and <c>/ws/subagent</c> were a LOGIN wall and nothing more.
/// <see cref="IdentityMiddleware"/> established that the caller was somebody, and the socket then
/// attached them to whatever <c>threadId</c> they named - rehydrating that conversation's transcript
/// and, worse, freezing the pooled agent's owner to the caller (#399). Naming another tenant's thread
/// id was enough to read it and to take it.
/// </para>
/// <para>
/// The decision itself is NOT made here. It is <see cref="ConversationAuthorizer"/>'s, exactly as it
/// is for the REST routes - this type only maps a thread id to its stored row, and a refusal to the
/// bytes that carry it across a handshake. That is deliberate: a second copy of the policy is how the
/// REST surface and the socket surface drift apart, which is the shape of the bug this closes.
/// </para>
/// <para>
/// <b>Refusal happens before <c>AcceptWebSocketAsync</c>, never after.</b> A refusal frame sent down an
/// accepted socket is not a refusal a browser can classify - <c>WebSocket.onerror</c> exposes neither
/// a status nor a body - and it would mean the pooled agent had already been touched. Refusing the
/// handshake keeps the failure in HTTP, where <see cref="IdentityMiddleware.RefusalCodeHeader"/>
/// already carries a machine-readable code (#342).
/// </para>
/// <para>
/// <b>Existence hiding.</b> The socket's analog of the REST 404 is a handshake refused with the SAME
/// 404 body <c>ConversationsController.UnknownThread</c> writes, down to the interpolated id and the
/// <c>unknown_thread</c> code. A never-minted thread id and another tenant's thread id must be
/// indistinguishable here for the same reason they must be indistinguishable there: thread ids are
/// enumerable across a deployment, so a distinguishable answer is an existence oracle. That is also
/// why this gate does NOT mint a row for an unknown id. Minting would make an unknown id succeed
/// while a taken id refused, which is precisely the oracle - see the operational note in
/// <c>docs/deployment/AUTH_ENFORCE.md</c> for what a client must do instead.
/// </para>
/// </remarks>
public sealed class WebSocketConversationGate
{
    private readonly ConversationAuthorizer _authorizer;
    private readonly IConversationStore _store;
    private readonly ILogger<WebSocketConversationGate> _logger;

    /// <summary>Creates the gate.</summary>
    /// <param name="authorizer">The one decision point, shared with the REST routes.</param>
    /// <param name="store">Conversation store, read for the row the decision needs.</param>
    /// <param name="logger">Logger for refusals.</param>
    public WebSocketConversationGate(
        ConversationAuthorizer authorizer,
        IConversationStore store,
        ILogger<WebSocketConversationGate> logger)
    {
        ArgumentNullException.ThrowIfNull(authorizer);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(logger);

        _authorizer = authorizer;
        _store = store;
        _logger = logger;
    }

    /// <summary>
    /// Decides whether the current request may bind <paramref name="threadId"/> to a socket, writing
    /// the handshake refusal itself when it may not.
    /// </summary>
    /// <remarks>
    /// <paramref name="action"/> is <see cref="AccessAction.Write"/> for <c>/ws</c> and that is a
    /// decision, not an oversight: the chat socket accepts user turns and freezes the pooled agent's
    /// owner to the caller, both of which are writes. A read-only grantee (a <c>viewer</c> share)
    /// therefore cannot open the chat socket and reads the conversation over REST instead. Admitting
    /// them on <see cref="AccessAction.Read"/> would let a viewer send messages, because nothing
    /// downstream of the handshake re-checks the action.
    /// </remarks>
    /// <param name="context">The handshake request.</param>
    /// <param name="threadId">The conversation the socket would attach to.</param>
    /// <param name="action">The action the socket confers.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True when the handshake may proceed; false when a refusal has been written.</returns>
    public async Task<bool> AdmitAsync(
        HttpContext context,
        string threadId,
        AccessAction action,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);

        if (!_authorizer.IsEnforced)
        {
            return true;
        }

        var metadata = await _store.LoadMetadataAsync(threadId, ct).ConfigureAwait(false);
        var result = await _authorizer.AuthorizeAsync(threadId, metadata, action, ct).ConfigureAwait(false);
        if (result.Allowed)
        {
            return true;
        }

        await RefuseAsync(context, threadId, result).ConfigureAwait(false);
        return false;
    }

    /// <summary>
    /// Decides whether the current request may attach to a spawned sub-agent's stream, writing the
    /// handshake refusal itself when it may not.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two independent checks, and both are needed. The FIRST is the parent conversation's own
    /// authorization, on <see cref="AccessAction.Read"/> - the sub-agent socket is presentation-only,
    /// it relays a child's stream and accepts no turns, so read on the parent is the right ask and
    /// matches what <c>GET /api/conversations/{threadId}/subagents</c> already demands.
    /// </para>
    /// <para>
    /// The SECOND is that the named child actually belongs to the named parent. Without it the first
    /// check is a formality: a caller passes their OWN parent thread id (which they are trivially
    /// authorized for) together with someone else's <c>agentId</c>. All three LIVE lookups in the
    /// handler are parent-scoped and would miss, but the handler then falls back to replaying
    /// <c>subagent-{agentId}</c> straight out of the store - so an authorized parent id becomes a
    /// passphrase for any child in the deployment. The durable parent link stamped by
    /// <see cref="SubAgentProvenance"/> is what closes that.
    /// </para>
    /// <para>
    /// A mismatched child does NOT refuse the handshake, and that is the point rather than a
    /// softening. Refusing it would make "not your child" answer differently from "no such child" -
    /// the latter accepts the socket and reports <c>subagent_unavailable</c> - and the difference is
    /// an existence oracle over sub-agent ids. Withholding the replay instead makes the two answers
    /// identical, frame for frame, which is the same existence-hiding convention the 404 implements
    /// on the REST surface.
    /// </para>
    /// <para>
    /// A child with NO metadata row at all keeps its replay, and that is not a hole left open: it is
    /// the live case, where the manager has spawned a child that has not persisted anything yet, and
    /// the handler resolves it through the parent's own <c>SubAgentManager</c>. Any child that has
    /// persisted has a row - the manager stamps provenance when it writes, and
    /// <c>ConversationOwnershipRepairHostedService</c> synthesizes a row at startup for message-only
    /// threads written before that. A row that exists but names a different parent, or names none at
    /// all, loses the replay.
    /// </para>
    /// </remarks>
    /// <param name="context">The handshake request.</param>
    /// <param name="parentThreadId">The parent conversation named in the query string.</param>
    /// <param name="agentId">The sub-agent named in the query string.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The admission, whose refusal (if any) has already been written to the response.</returns>
    public async Task<SubAgentSocketAdmission> AdmitSubAgentAsync(
        HttpContext context,
        string parentThreadId,
        string agentId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(parentThreadId);
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);

        if (!_authorizer.IsEnforced)
        {
            return new SubAgentSocketAdmission(true, true);
        }

        var parentMetadata = await _store.LoadMetadataAsync(parentThreadId, ct).ConfigureAwait(false);
        var parentResult = await _authorizer
            .AuthorizeAsync(parentThreadId, parentMetadata, AccessAction.Read, ct)
            .ConfigureAwait(false);
        if (!parentResult.Allowed)
        {
            await RefuseAsync(context, parentThreadId, parentResult).ConfigureAwait(false);
            return new SubAgentSocketAdmission(false, false);
        }

        var childThreadId = SubAgentProvenance.ThreadIdPrefix + agentId;
        var childMetadata = await _store.LoadMetadataAsync(childThreadId, ct).ConfigureAwait(false);
        if (childMetadata is null)
        {
            return new SubAgentSocketAdmission(true, true);
        }

        var stampedParent = SubAgentProvenance.TryProject(childMetadata)?.ParentThreadId;
        var isOwnChild = string.Equals(stampedParent, parentThreadId, StringComparison.Ordinal);
        if (!isOwnChild)
        {
            _logger.LogWarning(
                "Sub-agent {AgentId} is not a child of {ParentThreadId}; persisted replay withheld.",
                agentId,
                parentThreadId);
        }

        return new SubAgentSocketAdmission(true, isOwnChild);
    }

    /// <summary>
    /// Writes the handshake refusal. The 404 body is BYTE-IDENTICAL to
    /// <c>ConversationsController.UnknownThread</c>'s, deliberately: a client that can tell the socket's
    /// "not found" from the REST surface's has learned something about which of the two refused, and a
    /// client that can tell one socket refusal from another has an existence oracle. Do not vary it.
    /// </summary>
    /// <remarks>
    /// 403, never 401, for the non-hiding refusals - the same reasoning as
    /// <see cref="IdentityMiddleware.WebSocketRefusalCode"/>: 401 is the one status a browser answers
    /// by re-authenticating, and re-authenticating cannot attach a credential to a handshake that
    /// already carried one (#341, #342).
    /// </remarks>
    private async Task RefuseAsync(HttpContext context, string threadId, ConversationAccessResult result)
    {
        int status;
        string code;
        object body;

        if (result.HidesExistence)
        {
            _logger.LogWarning(
                "WebSocket handshake for {ThreadId} refused as unknown for the current principal: {Reason}",
                threadId,
                result.Reason);

            status = StatusCodes.Status404NotFound;
            code = "unknown_thread";
            body = new { error = $"Conversation '{threadId}' not found.", code };
        }
        else
        {
            _logger.LogWarning(
                "WebSocket handshake for {ThreadId} refused: {Reason}",
                threadId,
                result.Reason);

            status = StatusCodes.Status403Forbidden;
            code = result.Reason;
            body = new { error = "forbidden", code, threadId };
        }

        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.Headers[IdentityMiddleware.RefusalCodeHeader] = code;

        await context.Response.WriteAsync(JsonSerializer.Serialize(body)).ConfigureAwait(false);
    }
}
