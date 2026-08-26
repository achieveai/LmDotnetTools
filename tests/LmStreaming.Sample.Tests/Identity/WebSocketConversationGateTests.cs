using System.Text;
using AchieveAi.LmDotnetTools.LmCore.Identity;
using LmStreaming.Sample.Identity;
using LmStreaming.Sample.Tests.Controllers;
using LmStreaming.Sample.Tests.TestDoubles;
using Microsoft.AspNetCore.Http;

namespace LmStreaming.Sample.Tests.Identity;

/// <summary>
/// The BYTES of the WebSocket handshake refusals (#419).
/// </summary>
/// <remarks>
/// <para>
/// These belong here rather than in the E2E suite because a handshake exposes neither status nor body
/// to the client that made it - a browser's <c>WebSocket.onerror</c> carries nothing, and the test
/// host's WebSocket client surfaces only the status in an exception message. The E2E suite can prove
/// that a handshake is refused; only the gate itself can be asked what it wrote, and what it wrote is
/// the whole existence-hiding claim.
/// </para>
/// <para>
/// The claim: a thread id that names NOTHING and a thread id that names SOMEONE ELSE'S conversation
/// must be answered identically, down to the field order. Thread ids are enumerable across a
/// deployment, so any difference between the two makes <c>/ws</c> an existence oracle - which is
/// exactly why the gate refuses an unknown id instead of minting a row for it.
/// </para>
/// </remarks>
public sealed class WebSocketConversationGateTests
{
    private const string TenantA = "tnt_a";
    private const string TenantB = "tnt_b";
    private const string Bob = "dir-a:bob";
    private const string Alice = "dir-a:alice";

    [Fact]
    public async Task ARefusedThreadAndANeverMintedOne_AreAnsweredWithTheSameBytes()
    {
        const string AlicesThread = "thread-alices-private";
        const string NeverMinted = "thread-never-minted";

        var store = new InMemoryConversationStore();
        await SaveOwnedAsync(store, AlicesThread, TenantA, Alice);

        var refused = await RefuseAsync(store, AlicesThread, AsBob());
        var absent = await RefuseAsync(store, NeverMinted, AsBob());

        _ = refused.Status.Should().Be(StatusCodes.Status404NotFound);
        _ = absent.Status.Should().Be(refused.Status);
        _ = refused.RefusalCode.Should().Be("unknown_thread");
        _ = absent.RefusalCode.Should().Be(refused.RefusalCode);

        // The ONLY difference the two bodies are permitted is the id the caller supplied themselves.
        // Substituting it must make them identical - a distinct code, a different phrasing, even a
        // different field order would reopen the oracle.
        _ = refused.Body.Replace(AlicesThread, NeverMinted, StringComparison.Ordinal)
            .Should().Be(absent.Body);
    }

    /// <summary>
    /// The same body <c>ConversationsController.UnknownThread</c> writes, character for character. The
    /// two surfaces answer for the same thread ids, so a client that can tell the socket's refusal
    /// from the REST surface's has learned which of the two refused it.
    /// </summary>
    /// <remarks>
    /// Compared against a body the REST route ACTUALLY produced, by driving a real
    /// <c>ConversationsController</c> at an unknown thread. A third literal spelled out here would be
    /// the same hazard the shared factory exists to remove: it agrees with both surfaces on the day it
    /// is written, and after an edit to either one it agrees with neither while still passing.
    /// </remarks>
    [Fact]
    public async Task TheExistenceHidingRefusal_IsTheSameBodyTheRestSurfaceWrites()
    {
        const string ThreadId = "thread-probed";

        var refused = await RefuseAsync(new InMemoryConversationStore(), ThreadId, AsBob());

        await using var pool = ConversationsControllerTests.CreatePool();
        var rest = await ConversationsControllerTests
            .CreateController(
                new InMemoryConversationStore(),
                pool,
                ConversationsControllerTests.ModeStoreResolvingSystemModes())
            .SendMessage(ThreadId, new SendMessageRequest { Text = "probe" }, CancellationToken.None);

        var restBody = JsonSerializer.Serialize(Assert.IsType<NotFoundObjectResult>(rest).Value);

        // Serialised through the same call the gate makes, so what is compared is the two payloads and
        // not two pipelines' encoder settings. Field order is included on purpose: it survives into the
        // bytes, and a different order alone is enough to tell the two surfaces apart.
        _ = refused.Body.Should().Be(
            restBody,
            "a caller who can distinguish the socket's 404 from the REST route's has learned which of "
                + "the two refused, and from that whether the id names anything");
    }

    /// <summary>
    /// A refusal that is NOT existence-hiding keeps the 403 that already admits the id names
    /// something - and never a 401, because 401 is the one status a browser answers by
    /// re-authenticating, which cannot fix a handshake that already carried a credential (#341, #342).
    /// </summary>
    [Fact]
    public async Task ARefusalThatAlreadyAdmitsExistence_Is403AndCarriesItsOwnReason()
    {
        const string ThreadId = "thread-viewer-only";

        var store = new InMemoryConversationStore();
        await SaveOwnedAsync(store, ThreadId, TenantA, Alice);

        var grants = new InMemoryResourceGrantStore();
        await grants.GrantAsync(new ResourceGrant
        {
            TenantId = TenantA,
            Resource = ConversationAuthorizer.ConversationRef(ThreadId),
            SubjectId = Bob,
            Role = GrantRole.Viewer,
            GrantedBy = Alice,
            GrantedAt = DateTimeOffset.UtcNow,
        });

        // A VIEWER grantee: entitled to read the conversation, and therefore not someone the refusal
        // needs to hide it from - but /ws confers write, so the socket is still not theirs.
        var refused = await RefuseAsync(store, ThreadId, AsBob(), grants);

        _ = refused.Status.Should().Be(StatusCodes.Status403Forbidden);
        _ = refused.Status.Should().NotBe(StatusCodes.Status401Unauthorized);
        _ = refused.RefusalCode.Should().NotBe(
            "unknown_thread",
            "hiding existence from someone the host has already shown the conversation to buys "
                + "nothing and misdescribes the refusal");
        _ = refused.Body.Should().Contain("\"error\":\"forbidden\"");
    }

    [Fact]
    public async Task TheOwnersOwnThread_IsAdmittedAndNothingIsWritten()
    {
        const string ThreadId = "thread-alices-own";

        var store = new InMemoryConversationStore();
        await SaveOwnedAsync(store, ThreadId, TenantA, Alice);

        var context = NewContext();
        var admitted = await NewGate(store, AsAlice()).AdmitAsync(
            context, ThreadId, AccessAction.Write, CancellationToken.None);

        _ = admitted.Should().BeTrue();
        _ = BodyOf(context).Should().BeEmpty("an admitted handshake must write no refusal at all");
        _ = context.Response.Headers.ContainsKey(IdentityMiddleware.RefusalCodeHeader).Should().BeFalse();
    }

    /// <summary>
    /// With enforcement OFF the gate decides nothing, which is what keeps every pre-#419 host and
    /// test behaving exactly as it did. Asserted against a store with NO row for the thread - the
    /// input that would otherwise be the most certain refusal there is.
    /// </summary>
    [Fact]
    public async Task WithEnforcementOff_EveryThreadIsAdmitted()
    {
        var context = NewContext();
        var gate = new WebSocketConversationGate(
            TestAuthorizers.Disabled(),
            new InMemoryConversationStore(),
            NullLogger<WebSocketConversationGate>.Instance);

        var admitted = await gate.AdmitAsync(
            context, "thread-never-minted", AccessAction.Write, CancellationToken.None);

        _ = admitted.Should().BeTrue();
        _ = BodyOf(context).Should().BeEmpty();
    }

    [Fact]
    public async Task AChildOfAnotherParent_IsAdmittedButLosesItsReplay()
    {
        const string BobsParent = "thread-bobs-parent";
        const string AlicesParent = "thread-alices-parent";
        const string AgentId = "alices-child";

        var store = new InMemoryConversationStore();
        await SaveOwnedAsync(store, BobsParent, TenantA, Bob);
        await SaveChildAsync(store, AgentId, AlicesParent);

        var context = NewContext();
        var admission = await NewGate(store, AsBob()).AdmitSubAgentAsync(
            context, BobsParent, AgentId, CancellationToken.None);

        // Admitted, deliberately. Refusing the handshake would make "not your child" answer
        // differently from "no such child", which accepts the socket and reports
        // subagent_unavailable - and the difference is an oracle over sub-agent ids.
        _ = admission.Admitted.Should().BeTrue();
        _ = BodyOf(context).Should().BeEmpty();
        _ = admission.MayReplayPersistedTranscript.Should().BeFalse(
            "the caller is entitled to the parent they named, not to another parent's child");
    }

    [Fact]
    public async Task TheParentsOwnChild_KeepsItsReplay()
    {
        const string AlicesParent = "thread-alices-parent";
        const string AgentId = "alices-child";

        var store = new InMemoryConversationStore();
        await SaveOwnedAsync(store, AlicesParent, TenantA, Alice);
        await SaveChildAsync(store, AgentId, AlicesParent);

        var admission = await NewGate(store, AsAlice()).AdmitSubAgentAsync(
            NewContext(), AlicesParent, AgentId, CancellationToken.None);

        _ = admission.Admitted.Should().BeTrue();
        _ = admission.MayReplayPersistedTranscript.Should().BeTrue();
    }

    /// <summary>
    /// A child with no metadata row LOSES its replay. A row is not proof of "nothing persisted": the
    /// agent appends messages during a run and writes metadata only at completion, so a child running
    /// now - or killed mid-run - has a transcript and no row, and no repair pass ever synthesizes one.
    /// Granting the replay there is the oracle: a foreign mid-run child would answer differently from
    /// an agent id that names nothing.
    /// </summary>
    [Fact]
    public async Task AChildWithNoRowAtAll_LosesItsReplay()
    {
        const string AlicesParent = "thread-alices-parent";

        var store = new InMemoryConversationStore();
        await SaveOwnedAsync(store, AlicesParent, TenantA, Alice);

        var context = NewContext();
        var admission = await NewGate(store, AsAlice()).AdmitSubAgentAsync(
            context, AlicesParent, "brand-new-child", CancellationToken.None);

        // Still admitted: the handshake must not be where the difference shows up.
        _ = admission.Admitted.Should().BeTrue();
        _ = BodyOf(context).Should().BeEmpty();
        _ = admission.MayReplayPersistedTranscript.Should().BeFalse(
            "a missing row means the provenance could not be checked, not that nothing was persisted");
    }

    [Fact]
    public async Task AParentTheCallerCannotRead_RefusesTheHandshakeOutright()
    {
        const string AlicesParent = "thread-alices-parent";

        var store = new InMemoryConversationStore();
        await SaveOwnedAsync(store, AlicesParent, TenantA, Alice);

        var context = NewContext();
        var admission = await NewGate(store, AsBob()).AdmitSubAgentAsync(
            context, AlicesParent, "any-agent", CancellationToken.None);

        _ = admission.Admitted.Should().BeFalse();
        _ = context.Response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        _ = BodyOf(context).Should().Contain("unknown_thread");
    }

    /// <summary>One refusal, read as the bytes that left the gate.</summary>
    private sealed record Written(int Status, string? RefusalCode, string Body);

    private static async Task<Written> RefuseAsync(
        IConversationStore store,
        string threadId,
        Principal principal,
        IResourceGrantStore? grants = null)
    {
        var context = NewContext();
        var admitted = await NewGate(store, principal, grants).AdmitAsync(
            context, threadId, AccessAction.Write, CancellationToken.None);

        // Non-vacuity for every caller: a "refusal" that actually admitted would make every byte
        // assertion below compare two empty strings.
        _ = admitted.Should().BeFalse("this helper exists to capture a refusal");

        var code = context.Response.Headers.TryGetValue(
            IdentityMiddleware.RefusalCodeHeader, out var values)
            ? values.ToString()
            : null;

        return new Written(context.Response.StatusCode, code, BodyOf(context));
    }

    private static WebSocketConversationGate NewGate(
        IConversationStore store,
        Principal principal,
        IResourceGrantStore? grants = null) =>
        new(
            TestAuthorizers.Enforcing(principal, grants),
            store,
            NullLogger<WebSocketConversationGate>.Instance);

    private static DefaultHttpContext NewContext() =>
        new() { Response = { Body = new MemoryStream() } };

    private static string BodyOf(HttpContext context)
    {
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body, Encoding.UTF8, leaveOpen: true);
        return reader.ReadToEnd();
    }

    private static Principal AsBob() => AsUser(Bob);

    private static Principal AsAlice() => AsUser(Alice);

    private static Principal AsUser(string userId) =>
        new()
        {
            TenantId = TenantA,
            Actor = new PrincipalRef(PrincipalKind.EndUser, userId),
            Roles = new HashSet<string>(StringComparer.Ordinal),
            Source = PrincipalSource.Interactive,
        };

    private static Task SaveOwnedAsync(
        IConversationStore store,
        string threadId,
        string tenantId,
        string ownerUserId) =>
        store.SaveMetadataAsync(
            threadId,
            new ThreadMetadata
            {
                ThreadId = threadId,
                LastUpdated = 0,
                TenantId = tenantId,
                OwnerUserId = ownerUserId,
                Visibility = Visibility.Private,
            });

    private static Task SaveChildAsync(IConversationStore store, string agentId, string parentThreadId)
    {
        var childThreadId = SubAgentProvenance.ThreadIdPrefix + agentId;
        return store.SaveMetadataAsync(
            childThreadId,
            new ThreadMetadata
            {
                ThreadId = childThreadId,
                LastUpdated = 0,
                TenantId = TenantB,
                OwnerUserId = Alice,
                Visibility = Visibility.Private,
                Properties = SubAgentProvenance.Build(parentThreadId, snapshot: null),
            });
    }
}
