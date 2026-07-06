using System.Collections.Immutable;
using LmStreaming.Sample.Services;
using LmStreaming.Sample.Tests.Agents;
using LmStreaming.Sample.Tests.TestDoubles;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace LmStreaming.Sample.Tests.Controllers;

/// <summary>
/// Covers issue #153 M2's inbound S2S auth guard (<see cref="InboundS2SAuthAttribute"/>) and
/// <see cref="ConversationsController.SendMessage"/>'s caller-credential passthrough/conflict
/// mapping. Kept separate from <see cref="ConversationsControllerTests"/> (which predates this
/// slice) rather than folding in, per the task's file-separation instruction.
/// <para>
/// The attribute is an <see cref="IAsyncActionFilter"/> that only runs through the real MVC action
/// invocation pipeline, never when a test calls a controller action method directly. Scenarios (a)
/// and (b) below therefore invoke <see cref="InboundS2SAuthAttribute.OnActionExecutionAsync"/>
/// directly against a hand-built <see cref="ActionExecutingContext"/> — the smallest harness that
/// exercises the filter's actual logic without standing up a full <c>WebApplicationFactory</c>.
/// </para>
/// </summary>
public class ConversationsControllerS2SAuthTests
{
    private static ConversationsController CreateController(
        IConversationStore store,
        MultiTurnAgentPool pool,
        IChatModeStore modeStore,
        IWorkspaceStore? workspaceStore = null,
        ProviderRegistry? providerRegistry = null,
        ConversationStatusResolver? statusResolver = null)
    {
        return new ConversationsController(
            store,
            pool,
            modeStore,
            workspaceStore ?? Mock.Of<IWorkspaceStore>(),
            providerRegistry ?? new FakeProviderRegistry(defaultProviderId: "test", available: ["test"]).ToReal(),
            statusResolver ?? new ConversationStatusResolver(store, store as IRunLedgerStore ?? new InMemoryConversationStore()),
            NullLogger<ConversationsController>.Instance);
    }

    private static IChatModeStore ModeStoreResolvingSystemModes()
    {
        var modeStore = new Mock<IChatModeStore>();
        modeStore
            .Setup(m => m.GetModeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string modeId, CancellationToken _) => SystemChatModes.GetById(modeId));
        return modeStore.Object;
    }

    /// <summary>Builds a pool via the context-aware constructor so the factory can observe the
    /// <see cref="MultiTurnAgentPool.AgentCreationContext"/> (in particular its
    /// <c>CallerCredential</c>) for each creation, without needing a real provider registry or
    /// conversation store (both optional/nullable on this constructor).</summary>
    private static MultiTurnAgentPool CreatePoolCapturingCredential(
        Action<MultiTurnAgentPool.AgentCreationContext> onCreate)
    {
        return new MultiTurnAgentPool(
            context =>
            {
                onCreate(context);
                return new MultiTurnAgentPool.AgentCreationResult(new FakeMultiTurnAgent(context.ThreadId));
            },
            providerRegistry: null,
            conversationStore: null,
            NullLogger<MultiTurnAgentPool>.Instance);
    }

    private static Task SeedThreadMetadataForDefaultModeAsync(InMemoryConversationStore store, string threadId)
    {
        return store.SaveMetadataAsync(
            threadId,
            new ThreadMetadata
            {
                ThreadId = threadId,
                LastUpdated = 1,
                Properties = ImmutableDictionary<string, object>.Empty
                    .SetItem(MultiTurnAgentPool.ModePropertyKey, SystemChatModes.DefaultModeId),
            });
    }

    /// <summary>Wires a <see cref="DefaultHttpContext"/> carrying the given request headers onto
    /// the controller — the only way (short of a full pipeline) to make
    /// <c>HttpContext?.Request?.Headers</c> resolve to something inside <c>SendMessage</c>.</summary>
    private static void SetRequestHeaders(ConversationsController controller, IDictionary<string, string> headers)
    {
        var httpContext = new DefaultHttpContext();
        foreach (var (key, value) in headers)
        {
            httpContext.Request.Headers[key] = value;
        }

        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
    }

    // ---------------------------------------------------------------------------------------
    // InboundS2SAuthAttribute — direct filter invocation (scenarios a/b + never-leaks assertion)
    // ---------------------------------------------------------------------------------------

    private static DefaultHttpContext CreateHttpContextWithConfig(string? configuredSecret, string? presentedHeader)
    {
        var configData = new Dictionary<string, string?>();
        if (configuredSecret != null)
        {
            configData[InboundS2SAuthAttribute.SecretConfigKey] = configuredSecret;
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(configData).Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);

        var httpContext = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        if (presentedHeader != null)
        {
            httpContext.Request.Headers[InboundS2SAuthAttribute.HeaderName] = presentedHeader;
        }

        return httpContext;
    }

    private static ActionExecutingContext CreateActionExecutingContext(HttpContext httpContext)
    {
        var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());
        return new ActionExecutingContext(
            actionContext,
            [],
            new Dictionary<string, object?>(),
            controller: new object());
    }

    private static ActionExecutionDelegate CreateNextDelegate(ActionContext actionContext, Action onCalled)
    {
        return () =>
        {
            onCalled();
            return Task.FromResult(new ActionExecutedContext(actionContext, [], new object()));
        };
    }

    [Fact]
    public async Task Filter_Allows_WhenNoSecretConfigured()
    {
        var httpContext = CreateHttpContextWithConfig(configuredSecret: null, presentedHeader: null);
        var executingContext = CreateActionExecutingContext(httpContext);
        var nextCalled = false;
        var next = CreateNextDelegate(executingContext, () => nextCalled = true);

        await new InboundS2SAuthAttribute().OnActionExecutionAsync(executingContext, next);

        nextCalled.Should().BeTrue("the dev/keyless path must let the request through unmodified");
        executingContext.Result.Should().BeNull();
    }

    [Fact]
    public async Task Filter_Returns401_WhenSecretConfigured_AndHeaderMissing()
    {
        var httpContext = CreateHttpContextWithConfig(configuredSecret: "s3cr3t-inbound-value", presentedHeader: null);
        var executingContext = CreateActionExecutingContext(httpContext);
        var nextCalled = false;
        var next = CreateNextDelegate(executingContext, () => nextCalled = true);

        await new InboundS2SAuthAttribute().OnActionExecutionAsync(executingContext, next);

        nextCalled.Should().BeFalse();
        var unauthorized = Assert.IsType<UnauthorizedObjectResult>(executingContext.Result);
        unauthorized.StatusCode.Should().Be(401);
    }

    [Fact]
    public async Task Filter_Returns401_WhenSecretConfigured_AndHeaderWrong()
    {
        var httpContext = CreateHttpContextWithConfig(
            configuredSecret: "s3cr3t-inbound-value",
            presentedHeader: "totally-wrong-value");
        var executingContext = CreateActionExecutingContext(httpContext);
        var nextCalled = false;
        var next = CreateNextDelegate(executingContext, () => nextCalled = true);

        await new InboundS2SAuthAttribute().OnActionExecutionAsync(executingContext, next);

        nextCalled.Should().BeFalse();
        Assert.IsType<UnauthorizedObjectResult>(executingContext.Result);
    }

    [Fact]
    public async Task Filter_Allows_WhenSecretConfigured_AndHeaderMatches()
    {
        const string secret = "s3cr3t-inbound-value";
        var httpContext = CreateHttpContextWithConfig(configuredSecret: secret, presentedHeader: secret);
        var executingContext = CreateActionExecutingContext(httpContext);
        var nextCalled = false;
        var next = CreateNextDelegate(executingContext, () => nextCalled = true);

        await new InboundS2SAuthAttribute().OnActionExecutionAsync(executingContext, next);

        nextCalled.Should().BeTrue();
        executingContext.Result.Should().BeNull();
    }

    [Fact]
    public async Task Filter_UnauthorizedResponse_NeverContainsConfiguredSecretOrPresentedValue()
    {
        const string secret = "s3cr3t-inbound-value";
        const string wrongPresented = "totally-wrong-value";
        var httpContext = CreateHttpContextWithConfig(configuredSecret: secret, presentedHeader: wrongPresented);
        var executingContext = CreateActionExecutingContext(httpContext);
        var next = CreateNextDelegate(executingContext, onCalled: () => { });

        await new InboundS2SAuthAttribute().OnActionExecutionAsync(executingContext, next);

        var unauthorized = Assert.IsType<UnauthorizedObjectResult>(executingContext.Result);
        var payload = JsonSerializer.Serialize(unauthorized.Value);
        payload.Should().NotContain(secret);
        payload.Should().NotContain(wrongPresented);
        payload.Should().Contain("s2s_auth_failed");
    }

    // ---------------------------------------------------------------------------------------
    // SendMessage — caller-credential passthrough (scenario c) and cross-actor conflict (d)
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task SendMessage_BuildsCallerCredential_FromSbxHeaders_AndPassesToPool()
    {
        var store = new InMemoryConversationStore();
        const string threadId = "thread-s2s-caller-cred";
        await SeedThreadMetadataForDefaultModeAsync(store, threadId);

        MultiTurnAgentPool.AgentCreationContext? capturedContext = null;
        await using var pool = CreatePoolCapturingCredential(ctx => capturedContext = ctx);

        var controller = CreateController(store, pool, ModeStoreResolvingSystemModes());
        SetRequestHeaders(
            controller,
            new Dictionary<string, string>
            {
                ["X-Sbx-App-Id"] = "app-a",
                ["X-Sbx-App-Key"] = "a-key-value",
            });

        var result = await controller.SendMessage(threadId, new SendMessageRequest { Text = "hello" }, CancellationToken.None);

        Assert.IsType<AcceptedResult>(result);
        capturedContext.Should().NotBeNull();
        capturedContext!.CallerCredential.Should().Be(new SandboxCredential("app-a", "a-key-value"));

        // Belt-and-suspenders: the forwarded app key must never surface in the response body.
        var accepted = (AcceptedResult)result;
        JsonSerializer.Serialize(accepted.Value).Should().NotContain("a-key-value");
    }

    [Fact]
    public async Task SendMessage_PassesNullCallerCredential_WhenHttpContextUnset()
    {
        var store = new InMemoryConversationStore();
        const string threadId = "thread-s2s-no-caller-cred";
        await SeedThreadMetadataForDefaultModeAsync(store, threadId);

        MultiTurnAgentPool.AgentCreationContext? capturedContext = null;
        await using var pool = CreatePoolCapturingCredential(ctx => capturedContext = ctx);

        // Deliberately NOT wiring controller.ControllerContext — mirrors every SendMessage test in
        // ConversationsControllerTests.cs, and proves the HttpContext-unset path (no ambient HTTP
        // request, e.g. a direct unit-test invocation) is treated as "no caller credential" rather
        // than throwing a NullReferenceException.
        var controller = CreateController(store, pool, ModeStoreResolvingSystemModes());

        var result = await controller.SendMessage(threadId, new SendMessageRequest { Text = "hello" }, CancellationToken.None);

        Assert.IsType<AcceptedResult>(result);
        capturedContext.Should().NotBeNull();
        capturedContext!.CallerCredential.Should().BeNull();
    }

    [Fact]
    public async Task SendMessage_Returns409_OnCrossActorCredentialConflict_WithoutLeakingAppKeys()
    {
        var store = new InMemoryConversationStore();
        const string threadId = "thread-s2s-conflict";
        await SeedThreadMetadataForDefaultModeAsync(store, threadId);

        await using var pool = CreatePoolCapturingCredential(onCreate: _ => { });
        var mode = SystemChatModes.GetById(SystemChatModes.DefaultModeId)!;

        // Seed the thread's agent under caller A's identity, exactly as SendMessage itself would on
        // a prior request from that caller.
        _ = pool.GetOrCreateAgent(
            threadId,
            mode,
            requestedProviderId: null,
            requestResponseDumpFileName: null,
            requestedWorkspaceId: null,
            callerCredential: new SandboxCredential("app-a", "a-secret-key-value"));

        var controller = CreateController(store, pool, ModeStoreResolvingSystemModes());
        SetRequestHeaders(
            controller,
            new Dictionary<string, string>
            {
                ["X-Sbx-App-Id"] = "app-b",
                ["X-Sbx-App-Key"] = "b-secret-key-value",
            });

        var result = await controller.SendMessage(threadId, new SendMessageRequest { Text = "hello" }, CancellationToken.None);

        var conflict = Assert.IsType<ConflictObjectResult>(result);
        conflict.StatusCode.Should().Be(409);

        var payload = JsonSerializer.Serialize(conflict.Value);
        payload.Should().Contain("caller_credential_conflict");
        payload.Should().Contain(threadId);
        payload.Should().Contain("app-a");
        payload.Should().Contain("app-b");
        payload.Should().NotContain("a-secret-key-value");
        payload.Should().NotContain("b-secret-key-value");
    }
}
