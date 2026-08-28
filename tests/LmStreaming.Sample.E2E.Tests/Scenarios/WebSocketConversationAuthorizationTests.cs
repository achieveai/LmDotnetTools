using AchieveAi.LmDotnetTools.LmCore.Identity;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using AchieveAi.LmDotnetTools.LmTestUtils.Logging;
using AchieveAi.LmDotnetTools.LmTestUtils.TestMode;
using FluentAssertions;
using LmStreaming.Sample.E2E.Tests.Infrastructure;
using LmStreaming.Sample.Identity;
using LmStreaming.Sample.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace LmStreaming.Sample.E2E.Tests.Scenarios;

/// <summary>
/// The per-conversation authorization on the WebSocket transports (#419), against the real host.
/// </summary>
/// <remarks>
/// <para>
/// #342 put <c>/ws</c> and <c>/ws/subagent</c> inside the identity boundary, which changed who may
/// TRY from "anyone" to "any signed-in principal in the deployment". It added no per-conversation
/// check, so naming another tenant's thread id was enough to rehydrate its transcript and to freeze
/// the pooled agent to yourself. These tests are about the second half - the caller here is always
/// authenticated, and always somebody the host is happy to talk to.
/// </para>
/// <para>
/// Two refusal shapes, deliberately different from each other. <c>/ws</c> refuses the HANDSHAKE, so
/// nothing is created and no socket is accepted. <c>/ws/subagent</c> refuses the handshake only when
/// the PARENT is not the caller's; a child whose provenance does not check out - one stamped with a
/// different parent, and equally one with no metadata row to stamp - loses its persisted replay while
/// the socket still opens, because refusing that handshake would make "not your child" and "no such
/// child" tell apart - see <see cref="SubAgentSocketAdmission"/>.
/// </para>
/// </remarks>
public sealed class WebSocketConversationAuthorizationTests : LoggingTestBase
{
    private const string Tenant = "tnt_daemon";
    private const string Alice = "dir-a:alice";
    private const string Bob = "dir-b:bob";

    public WebSocketConversationAuthorizationTests(ITestOutputHelper output)
        : base(output) { }

    [Fact]
    public async Task WithEnforcementOn_ASocketNamingAnotherUsersThread_IsRefusedBeforeTheHandshake()
    {
        LogTestStart();
        using var factory = NewFactory();
        const string AlicesThread = "thread-alices-private";

        await ProvisionOwnedThreadAsync(factory, AlicesThread, Alice);

        // Bob is signed in. Before #419 that was the entire check, and this handshake completed.
        Func<Task> handshake = () => factory.ConnectWebSocketAsync(AlicesThread, subProtocols: CredentialFor(Bob));

        _ = await handshake
            .Should()
            .ThrowAsync<InvalidOperationException>(
                "a signed-in caller with no grant on the conversation must not get a socket on it"
            );

        // The refusal must also leave NOTHING behind. #399's owner freeze is per pooled entry, so a
        // refusal that still created the entry would hand Bob the freeze on Alice's thread - the
        // ownership half of the bug rather than the disclosure half.
        var pool =
            factory.Services.GetRequiredService<AchieveAi.LmDotnetTools.LmAgentInfra.Agents.MultiTurnAgentPool>();
        _ = pool.TryGet(AlicesThread, out _)
            .Should()
            .BeFalse("a refused handshake must not create the thread's pooled agent");
    }

    [Fact]
    public async Task WithEnforcementOn_ANeverMintedThread_IsRefusedToo()
    {
        LogTestStart();
        using var factory = NewFactory();

        // The other half of the existence-hiding convention, and the reason the gate does NOT mint a
        // row for an unknown id: if it did, an unknown id would open a socket while a taken one
        // refused, and the pair would be an existence oracle over a deployment's thread ids. The
        // refusal BYTES are pinned at the unit level, in WebSocketConversationGateTests - the
        // handshake exposes neither status nor body to a browser, so only the gate can be asked.
        Func<Task> handshake = () =>
            factory.ConnectWebSocketAsync("thread-never-minted-aaa", subProtocols: CredentialFor(Bob));

        _ = await handshake
            .Should()
            .ThrowAsync<InvalidOperationException>(
                "an id that names nothing must refuse exactly as an id that names someone else's "
                    + "conversation - the client provisions through POST /api/conversations first"
            );
    }

    [Fact]
    public async Task WithEnforcementOn_ASocketNamingTheCallersOwnThread_StillConnects()
    {
        LogTestStart();
        using var factory = NewFactory();
        const string AlicesThread = "thread-alices-own";

        await ProvisionOwnedThreadAsync(factory, AlicesThread, Alice);

        using var socket = await factory.ConnectWebSocketAsync(AlicesThread, subProtocols: CredentialFor(Alice));

        _ = socket.State.Should().Be(System.Net.WebSockets.WebSocketState.Open);

        await socket.CloseAsync(
            System.Net.WebSockets.WebSocketCloseStatus.NormalClosure,
            "done",
            CancellationToken.None
        );
    }

    [Fact]
    public async Task WithEnforcementOn_TheSubAgentSocket_RefusesAParentTheCallerCannotRead()
    {
        LogTestStart();
        using var factory = NewFactory();
        const string AlicesParent = "thread-alices-parent";

        await ProvisionOwnedThreadAsync(factory, AlicesParent, Alice);

        Func<Task> handshake = () =>
            factory.ConnectSubAgentWebSocketAsync(AlicesParent, "agent-1", subProtocols: CredentialFor(Bob));

        _ = await handshake
            .Should()
            .ThrowAsync<InvalidOperationException>(
                "the sub-agent socket relays the parent conversation's content and must ask the parent's "
                    + "own authorization first"
            );

        // Non-vacuity: the same handshake by the parent's OWNER must complete, or a host that
        // refused every sub-agent handshake would satisfy the assertion above.
        using var socket = await factory.ConnectSubAgentWebSocketAsync(
            AlicesParent,
            "agent-1",
            subProtocols: CredentialFor(Alice)
        );
        _ = socket.State.Should().Be(System.Net.WebSockets.WebSocketState.Open);
    }

    [Fact]
    public async Task WithEnforcementOn_AChildOfAnotherParent_AnswersExactlyLikeAChildThatDoesNotExist()
    {
        LogTestStart();
        using var factory = NewFactory();
        const string AlicesParent = "thread-alices-parent";
        const string BobsParent = "thread-bobs-parent";
        const string AlicesAgentId = "alices-child-agent";

        await ProvisionOwnedThreadAsync(factory, AlicesParent, Alice);
        await ProvisionOwnedThreadAsync(factory, BobsParent, Bob);
        await SeedPersistedChildAsync(factory, AlicesAgentId, AlicesParent);

        // Bob names his OWN parent - which he is trivially authorized for - together with Alice's
        // agent id. All three live lookups are parent-scoped and miss, so before #419 the handler
        // fell straight through to replaying "subagent-{agentId}" out of the store: Bob's own thread
        // id was a passphrase for any child in the deployment.
        var socket = await factory.ConnectSubAgentWebSocketAsync(
            BobsParent,
            AlicesAgentId,
            subProtocols: CredentialFor(Bob)
        );
        await using var client = new WebSocketTestClient(socket);

        using var frames = await client.CollectUntilDoneAsync(TimeSpan.FromSeconds(15));

        _ = frames
            .OfMessageType("done")
            .Should()
            .BeEmpty(
                "the done sentinel is what a caller entitled to the replay receives, and receiving it "
                    + "would tell Bob the agent id names something"
            );
        var error = frames.SingleOrDefault(frame =>
            frame.RootElement.TryGetProperty("code", out var code)
            && string.Equals(code.GetString(), "subagent_unavailable", StringComparison.Ordinal)
        );
        _ = error
            .Should()
            .NotBeNull("a child that is not this parent's must answer exactly as an agent id that names nothing");

        // ...and the socket closes, as it does for a genuinely unknown agent. A held-open socket
        // would be the same oracle in a different field.
        _ = socket.State.Should().NotBe(System.Net.WebSockets.WebSocketState.Open);
    }

    /// <summary>
    /// The non-vacuity leg of the test above: the replay this withholds from Bob must still reach
    /// Alice, or the assertion there would pass on a host whose replay was simply broken.
    /// </summary>
    [Fact]
    public async Task WithEnforcementOn_TheParentsOwnChild_StillReplaysItsPersistedTranscript()
    {
        LogTestStart();
        using var factory = NewFactory();
        const string AlicesParent = "thread-alices-parent";
        const string AlicesAgentId = "alices-child-agent";

        await ProvisionOwnedThreadAsync(factory, AlicesParent, Alice);
        await SeedPersistedChildAsync(factory, AlicesAgentId, AlicesParent);

        var socket = await factory.ConnectSubAgentWebSocketAsync(
            AlicesParent,
            AlicesAgentId,
            subProtocols: CredentialFor(Alice)
        );
        await using var client = new WebSocketTestClient(socket);

        using var frames = await client.CollectUntilDoneAsync(TimeSpan.FromSeconds(15));

        _ = frames
            .OfMessageType("done")
            .Should()
            .ContainSingle("the owning parent's completed child settles the focused client with the done sentinel");
        _ = socket
            .State.Should()
            .Be(System.Net.WebSockets.WebSocketState.Open, "read-only replay holds the socket open");
    }

    /// <summary>
    /// The case a metadata row cannot speak for: a child that is RUNNING, or that was killed mid-run.
    /// The agent appends each message as it produces it and writes metadata only when the run
    /// completes, so such a child has a transcript and no row - permanently, once it is killed, since
    /// no repair pass synthesizes a row for a message-only thread. Admitting the replay whenever the
    /// row was missing therefore leaked exactly the transcripts whose provenance could not be checked.
    /// </summary>
    [Fact]
    public async Task WithEnforcementOn_AMidRunChildWithNoRow_AnswersExactlyLikeAnAgentIdThatNamesNothing()
    {
        LogTestStart();
        using var factory = NewFactory();
        const string BobsParent = "thread-bobs-parent";

        // Fresh agent ids per run, deliberately. The host's FileConversationStore lives in one
        // process-wide directory that no test cleans, so a fixed id accumulates messages from earlier
        // runs AND can pick up a metadata row written by one of them - which silently destroys this
        // test's precondition, because a child WITH a row takes the provenance branch instead of the
        // no-row branch this case exists to pin.
        var alicesAgentId = $"alices-midrun-{Guid.NewGuid():N}";
        var neverExistedAgentId = $"never-existed-{Guid.NewGuid():N}";

        await ProvisionOwnedThreadAsync(factory, BobsParent, Bob);
        await SeedMidRunChildAsync(factory, alicesAgentId);

        // Both halves of the precondition, asserted rather than assumed. Leg A is only interesting if
        // the transcript it must NOT disclose is genuinely there, and only exercises the no-row branch
        // if there is genuinely no row: with a row present the comparison still passes, for the wrong
        // reason, and no mutation of the no-row branch can redden it.
        var store = factory.Services.GetRequiredService<IConversationStore>();
        var childThreadId = SubAgentProvenance.ThreadIdPrefix + alicesAgentId;
        _ = (await store.LoadMessagesAsync(childThreadId))
            .Should()
            .NotBeEmpty("the mid-run child must have a transcript for withholding it to mean anything");
        _ = (await store.LoadMetadataAsync(childThreadId))
            .Should()
            .BeNull(
                "this case is about a child with NO metadata row - a row would route it down the "
                    + "provenance branch instead"
            );

        var midRun = await AnswerForAsync(factory, BobsParent, alicesAgentId);
        var nothing = await AnswerForAsync(factory, BobsParent, neverExistedAgentId);

        _ = midRun
            .Frames.Should()
            .Equal(
                nothing.Frames,
                "a foreign child mid-run and an agent id that names nothing must be indistinguishable "
                    + "frame for frame - the done sentinel on one and an error on the other is an "
                    + "existence oracle over sub-agent ids"
            );
        _ = midRun
            .StillOpen.Should()
            .Be(
                nothing.StillOpen,
                "a held-open socket for one and a closed socket for the other is the same oracle read "
                    + "off the transport instead of off the frames"
            );
    }

    /// <summary>What one sub-agent handshake answered: its frames, with the caller's own agent id
    /// normalized out (the server echoes it back, and the caller supplied it), and whether the socket
    /// was left open.</summary>
    private sealed record SubAgentAnswer(IReadOnlyList<string> Frames, bool StillOpen);

    private static async Task<SubAgentAnswer> AnswerForAsync(
        E2EWebAppFactory factory,
        string parentThreadId,
        string agentId
    )
    {
        var socket = await factory.ConnectSubAgentWebSocketAsync(
            parentThreadId,
            agentId,
            subProtocols: CredentialFor(Bob)
        );
        await using var client = new WebSocketTestClient(socket);

        using var frames = await client.CollectUntilDoneAsync(TimeSpan.FromSeconds(15));

        var normalized = frames
            .Select(frame => frame.RootElement.GetRawText().Replace(agentId, "<agent>", StringComparison.Ordinal))
            .ToList();

        return new SubAgentAnswer(normalized, socket.State == System.Net.WebSockets.WebSocketState.Open);
    }

    /// <summary>
    /// Seeds a child that has persisted messages and NO metadata row - what the store holds while a
    /// run is in flight, and what it keeps holding if that run never completes.
    /// </summary>
    private static async Task SeedMidRunChildAsync(E2EWebAppFactory factory, string agentId)
    {
        var store = factory.Services.GetRequiredService<IConversationStore>();
        var childThreadId = SubAgentProvenance.ThreadIdPrefix + agentId;

        var persisted = MessagePersistenceConverter.ToPersistedMessage(
            new TextMessage { Role = Role.Assistant, Text = "half-written-child-answer" },
            childThreadId,
            runId: "run-1"
        );
        await store.AppendMessagesAsync(childThreadId, [persisted]);
    }

    /// <summary>
    /// Writes the row <c>POST /api/conversations</c> would write for <paramref name="userId"/>, so a
    /// test can own a conversation without driving provisioning through the REST surface it is not
    /// testing. Ownership is stamped exactly as <c>ConversationAuthorizer.StampOwnership</c> stamps
    /// it - tenant, owner, private - because a row missing any of those is refused for a DIFFERENT
    /// reason and would make these tests pass for the wrong one.
    /// </summary>
    private static Task ProvisionOwnedThreadAsync(E2EWebAppFactory factory, string threadId, string userId)
    {
        var store = factory.Services.GetRequiredService<IConversationStore>();
        return store.SaveMetadataAsync(
            threadId,
            new ThreadMetadata
            {
                ThreadId = threadId,
                LastUpdated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                TenantId = Tenant,
                OwnerUserId = userId,
                Visibility = Visibility.Private,
            }
        );
    }

    /// <summary>
    /// Seeds a COMPLETED sub-agent: a persisted transcript under <c>subagent-{agentId}</c> plus the
    /// durable parent link <see cref="SubAgentProvenance"/> stamps. Both halves matter - the
    /// transcript is what the handler would replay, and the link is what says whose it is.
    /// </summary>
    private static async Task SeedPersistedChildAsync(E2EWebAppFactory factory, string agentId, string parentThreadId)
    {
        var store = factory.Services.GetRequiredService<IConversationStore>();
        var childThreadId = SubAgentProvenance.ThreadIdPrefix + agentId;

        var persisted = MessagePersistenceConverter.ToPersistedMessage(
            new TextMessage { Role = Role.Assistant, Text = "persisted-child-answer" },
            childThreadId,
            runId: "run-1"
        );
        await store.AppendMessagesAsync(childThreadId, [persisted]);

        await store.SaveMetadataAsync(
            childThreadId,
            new ThreadMetadata
            {
                ThreadId = childThreadId,
                LastUpdated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                TenantId = Tenant,
                OwnerUserId = Alice,
                Visibility = Visibility.Private,
                Properties = SubAgentProvenance.Build(parentThreadId, snapshot: null),
            }
        );
    }

    /// <summary>
    /// The subprotocol list a signed-in browser offers: the credential, then the application
    /// subprotocol the server is allowed to echo back.
    /// </summary>
    private static string[] CredentialFor(string userId) =>
        [IdentityMiddleware.WebSocketCredentialSubProtocolPrefix + userId, IdentityMiddleware.WebSocketSubProtocol];

    private static E2EWebAppFactory NewFactory()
    {
        var responder = ScriptedSseResponder.New().ForRole("noop", _ => true).Turn(t => t.Text("ok")).Build();

        return new E2EWebAppFactory(
            "test",
            new ScriptedBuilder(responder.AsAnthropicHandler()),
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Identity:Enforce"] = "true",
                ["Identity:DatabasePath"] = Path.Combine(Path.GetTempPath(), $"identity_ws419_{Guid.NewGuid():N}.db"),
            },
            services => services.AddSingleton<IRequestPrincipalSource, BearerUserPrincipalSource>()
        );
    }

    /// <summary>
    /// Turns <c>Authorization: Bearer &lt;userId&gt;</c> into an end-user principal, through the REAL
    /// extension point <see cref="IdentityMiddleware"/> consults rather than a hand-placed stash - so a
    /// test credential travels the same path a token does.
    /// </summary>
    private sealed class BearerUserPrincipalSource : IRequestPrincipalSource
    {
        public ValueTask<PrincipalResolution?> ResolveAsync(HttpContext context, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(context);

            var header = context.Request.Headers.Authorization.ToString();
            if (!header.StartsWith("Bearer ", StringComparison.Ordinal))
            {
                return ValueTask.FromResult<PrincipalResolution?>(null);
            }

            var userId = header["Bearer ".Length..].Trim();
            if (userId.Length == 0)
            {
                return ValueTask.FromResult<PrincipalResolution?>(null);
            }

            return ValueTask.FromResult<PrincipalResolution?>(
                PrincipalResolution.Success(
                    new Principal
                    {
                        TenantId = Tenant,
                        Actor = new PrincipalRef(PrincipalKind.EndUser, userId),
                        Source = PrincipalSource.Interactive,
                    }
                )
            );
        }
    }
}
