using LmStreaming.Sample.Services;
using LmStreaming.Sample.Tests.Agents;
using LmStreaming.Sample.Tests.TestDoubles;
using Microsoft.AspNetCore.Http;

namespace LmStreaming.Sample.Tests.Controllers;

/// <summary>
/// Provisioning is the ONLY way a conversation acquires the typed review-publication surface, so it is
/// the only place the grant can be checked. These scenarios pin the three answers that matter: an
/// authenticated complete scope is stored in a form the agent build reads back, an unauthenticated one
/// is REFUSED rather than dropped, and an incomplete one never becomes a half-addressed authorization.
/// </summary>
public class ConversationsControllerReviewScopeTests
{
    private static ReviewPublicationScopeRequest ValidScope() =>
        new()
        {
            RoundId = 7,
            Provider = "github",
            RepoId = 1234,
            PrId = "118",
            ExpectedHeadSha = "abc123",
            LivePostingAuthorized = true,
        };

    [Fact]
    public async Task Provision_OverS2S_PersistsTheScope_AndTheAgentBuildReadsItBack()
    {
        // The round trip is the point: storing the property would pass even if nothing ever read it,
        // which is exactly the state SystemPromptAppendix sat in for its whole life (#528/#529). The
        // assertion goes through ReadAsync — the same call Program.cs makes when it builds the agent.
        var (controller, store) = CreateController(serviceToService: true);

        var result = await controller.Provision(Request(ValidScope()), CancellationToken.None);

        var threadId = Assert
            .IsType<ProvisionConversationResponse>(Assert.IsType<OkObjectResult>(result).Value)
            .ThreadId;

        var readBack = await ReviewPublicationScope.ReadAsync(store, threadId, CancellationToken.None);
        readBack.Should().NotBeNull();
        readBack!.RoundId.Should().Be(7);
        readBack.Provider.Should().Be("github");
        readBack.RepoId.Should().Be(1234);
        readBack.PrId.Should().Be("118");
        readBack.ExpectedHeadSha.Should().Be("abc123");
        readBack
            .LivePostingAuthorized.Should()
            .BeTrue("the daemon's live-write authorization must survive the persist/read round trip");
    }

    /// <summary>
    /// The same-origin browser path deliberately carries no S2S marker, so without this check the SPA
    /// could mint a conversation able to write to a pull request under the daemon's credentials.
    /// Refused, not silently dropped: a dropped scope mints a conversation the caller believes can
    /// publish and that never will.
    /// </summary>
    [Fact]
    public async Task Provision_WithoutTheS2SMarker_RefusesAScope_AndStoresNothing()
    {
        var (controller, store) = CreateController(serviceToService: false);

        var result = await controller.Provision(Request(ValidScope()), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
        (await store.ListThreadsAsync(0, 100, null, CancellationToken.None))
            .Should()
            .BeEmpty("a refused provision must not leave a conversation behind");
    }

    [Theory]
    [InlineData(0, "github", 1234, "118", "abc123")] // no round
    [InlineData(-1, "github", 1234, "118", "abc123")] // negative round
    [InlineData(7, null, 1234, "118", "abc123")] // no provider
    [InlineData(7, "github", 0, "118", "abc123")] // no repo
    [InlineData(7, "github", 1234, "  ", "abc123")] // blank PR id
    [InlineData(7, "github", 1234, "118", null)] // no expected head
    public async Task Provision_RefusesAnIncompleteScope(
        long roundId,
        string? provider,
        long repoId,
        string? prId,
        string? expectedHead
    )
    {
        var (controller, _) = CreateController(serviceToService: true);

        var result = await controller.Provision(
            Request(
                new ReviewPublicationScopeRequest
                {
                    RoundId = roundId,
                    Provider = provider,
                    RepoId = repoId,
                    PrId = prId,
                    ExpectedHeadSha = expectedHead,
                }
            ),
            CancellationToken.None
        );

        Assert.IsType<BadRequestObjectResult>(result);
    }

    /// <summary>
    /// The default path, and the one that must stay unchanged: every conversation the UI creates gets no
    /// publication surface at all, and provisioning without a scope still works over the browser path
    /// where <c>Request</c> carries no S2S marker.
    /// </summary>
    [Fact]
    public async Task Provision_WithoutAScope_SucceedsAndGrantsNoPublicationSurface()
    {
        var (controller, store) = CreateController(serviceToService: false);

        var result = await controller.Provision(Request(scope: null), CancellationToken.None);

        var threadId = Assert
            .IsType<ProvisionConversationResponse>(Assert.IsType<OkObjectResult>(result).Value)
            .ThreadId;

        (await ReviewPublicationScope.ReadAsync(store, threadId, CancellationToken.None)).Should().BeNull();
    }

    // --- Harness ----------------------------------------------------------------------------------

    private static ProvisionConversationRequest Request(ReviewPublicationScopeRequest? scope) =>
        new()
        {
            WorkspaceId = "ws-1",
            ProviderId = "test",
            ModeId = SystemChatModes.CodeReviewDaemonModeId,
            ReviewScope = scope is null ? null : new ReviewConversationScope("17", scope.RoundId.ToString()),
            ReviewPublicationScope = scope,
        };

    /// <summary>
    /// A controller over an in-memory store, with an <see cref="HttpContext"/> that either does or does
    /// not present an S2S surface marker — the exact discriminator
    /// <see cref="InboundS2SAuthAttribute.IsServiceToServiceRequest"/> reads.
    /// </summary>
    private static (ConversationsController Controller, InMemoryConversationStore Store) CreateController(
        bool serviceToService
    )
    {
        var store = new InMemoryConversationStore();
        var pool = new MultiTurnAgentPool(
            context => new MultiTurnAgentPool.AgentCreationResult(new FakeMultiTurnAgent(context.ThreadId)),
            new FakeProviderRegistry(defaultProviderId: "test", available: ["test"]).ToReal(),
            store,
            NullLogger<MultiTurnAgentPool>.Instance
        );

        var modeStore = new Mock<IChatModeStore>();
        _ = modeStore
            .Setup(m => m.GetModeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string modeId, CancellationToken _) => SystemChatModes.GetById(modeId));

        var workspaceStore = new Mock<IWorkspaceStore>();
        _ = workspaceStore
            .Setup(w => w.GetAsync("ws-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(
                new Workspace
                {
                    Id = "ws-1",
                    Name = "ws-1",
                    DirectoryRelPath = "ws-1",
                }
            );

        var httpContext = new DefaultHttpContext();
        if (serviceToService)
        {
            httpContext.Request.Headers[InboundS2SAuthAttribute.HeaderName] = "presented-secret";
        }

        var controller = new ConversationsController(
            store,
            pool,
            modeStore.Object,
            workspaceStore.Object,
            new FakeProviderRegistry(defaultProviderId: "test", available: ["test"]).ToReal(),
            new ConversationStatusResolver(store, store),
            TimeProvider.System,
            new WorkflowRunRegistry(),
            TestAuthorizers.Disabled(),
            NullLogger<ConversationsController>.Instance,
            NullLogger<AgentHierarchyService>.Instance,
            new SubAgentScanCoverageCache(),
            new ConversationDescendantScanner(store, NullLogger<ConversationDescendantScanner>.Instance),
            new ReviewAuditBridge(
                new HttpClient(new Mock<HttpMessageHandler>().Object)
                {
                    BaseAddress = new Uri("http://review-audit.test/"),
                },
                "test-review-audit-secret"
            )
        )
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
        };

        return (controller, store);
    }
}
