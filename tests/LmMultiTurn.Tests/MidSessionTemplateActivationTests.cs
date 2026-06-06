using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using FluentAssertions;
using Moq;
using Xunit;

namespace LmMultiTurn.Tests;

/// <summary>
/// End-to-end integration test for issue #77: a template registered into the shared
/// <see cref="MutableSubAgentTemplateSource"/> mid-session must surface in the request options
/// that <see cref="ToolCallInjectionMiddleware"/> passes to the underlying agent on the NEXT
/// turn — without rebuilding the middleware stack. This pins the full chain
/// <c>MutableSubAgentTemplateSource → SubAgentToolProvider → FunctionRegistry.BuildContracts →
/// ToolCallInjectionMiddleware</c> as one wired path, so a regression in any link breaks here.
/// </summary>
public class MidSessionTemplateActivationTests : IAsyncLifetime
{
    private readonly Mock<IMultiTurnAgent> _parentMock = new();
    private readonly Mock<IStreamingAgent> _subAgentMock = new();
    private SubAgentManager? _manager;
    private MutableSubAgentTemplateSource? _source;
    private ToolCallInjectionMiddleware? _middleware;

    public Task InitializeAsync()
    {
        _parentMock
            .Setup(p => p.SendAsync(
                It.IsAny<List<IMessage>>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SendReceipt("receipt-1", null, DateTimeOffset.UtcNow));

        var seed = new SubAgentTemplate
        {
            Name = "researcher",
            SystemPrompt = "You are a researcher.",
            Description = "Researches topics.",
            WhenToUse = "Use for investigation.",
            AgentFactory = () => _subAgentMock.Object,
        };

        var options = new SubAgentOptions
        {
            Templates = new Dictionary<string, SubAgentTemplate> { ["researcher"] = seed },
            MaxConcurrentSubAgents = 5,
        };

        _source = new MutableSubAgentTemplateSource(options.Templates);

        _manager = new SubAgentManager(
            parentAgent: _parentMock.Object,
            parentContracts: [],
            parentHandlers: new Dictionary<string, ToolHandler>(),
            options: options,
            source: _source);

        var provider = new SubAgentToolProvider(_manager, _source);
        var registry = new FunctionRegistry().AddProvider(provider);

        var (middleware, _) = registry.BuildToolCallComponents();
        _middleware = middleware;

        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_manager != null)
        {
            await _manager.DisposeAsync();
        }
    }

    [Fact]
    public async Task TryRegister_MidSession_SurfacesInNextTurnRequestOptions()
    {
        // Pin the end-to-end mid-session activation flow. Turn 1: only the seeded template is
        // visible in the Agent tool description. Webhook fires (simulated by a TryRegister call
        // on the same source). Turn 2: the new template surfaces in the request options that
        // hit the agent — without recreating any middleware.
        var capturedOptions = new List<GenerateReplyOptions?>();
        var agentMock = new Mock<IAgent>();
        agentMock
            .Setup(a => a.GenerateReplyAsync(
                It.IsAny<IEnumerable<IMessage>>(),
                It.IsAny<GenerateReplyOptions>(),
                It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<IMessage>, GenerateReplyOptions, CancellationToken>(
                (_, opts, _) => capturedOptions.Add(opts))
            .ReturnsAsync([new TextMessage { Text = "ok", Role = Role.Assistant }]);

        var ctx = new MiddlewareContext([new TextMessage { Text = "hi", Role = Role.User }]);

        _ = await _middleware!.InvokeAsync(ctx, agentMock.Object);

        var turnOneAgent = capturedOptions[0]!.Functions!.First(f => f.Name == "Agent");
        turnOneAgent.Description.Should().Contain("researcher");
        turnOneAgent.Description.Should().NotContain("reviewer");

        // Discovery webhook arrives mid-session.
        _source!.TryRegister("reviewer", new SubAgentTemplate
        {
            Name = "reviewer",
            SystemPrompt = "You are a reviewer.",
            Description = "Reviews PRs.",
            WhenToUse = "Use after coder lands a change.",
            AgentFactory = () => _subAgentMock.Object,
        }).Should().BeTrue();

        _ = await _middleware!.InvokeAsync(ctx, agentMock.Object);

        var turnTwoAgent = capturedOptions[1]!.Functions!.First(f => f.Name == "Agent");
        turnTwoAgent.Description.Should().Contain("researcher");
        turnTwoAgent.Description.Should().Contain("reviewer");
        turnTwoAgent.Description.Should().Contain("Reviews PRs.");
    }

    [Fact]
    public async Task MultiTurnAgentLoop_ConstructedWithSharedSource_NextTurnSeesActivatedTemplate()
    {
        // Pin the Program.cs wiring decision: passing a `subAgentTemplateSource` to the loop ctor
        // must route through SubAgentToolProvider so a later TryRegister surfaces on the next turn
        // hitting the provider agent. If a future refactor drops the `?? new MutableSubAgentTemplateSource`
        // fallback OR ignores `subAgentTemplateSource` and constructs its own private source, the
        // unit-level provider tests still pass but this test fails — pinning the chain at the seam
        // the production sample actually uses.
        var capturedOptions = new List<GenerateReplyOptions?>();
        var providerAgent = new Mock<IStreamingAgent>();
        providerAgent
            .Setup(a => a.GenerateReplyStreamingAsync(
                It.IsAny<IEnumerable<IMessage>>(),
                It.IsAny<GenerateReplyOptions>(),
                It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<IMessage>, GenerateReplyOptions, CancellationToken>(
                (_, opts, _) => capturedOptions.Add(opts))
            .Returns<IEnumerable<IMessage>, GenerateReplyOptions, CancellationToken>(
                (_, _, _) => Task.FromResult(SingleTextReply($"turn-{capturedOptions.Count}")));

        var sharedSource = new MutableSubAgentTemplateSource(
            new Dictionary<string, SubAgentTemplate>(StringComparer.Ordinal)
            {
                ["researcher"] = new SubAgentTemplate
                {
                    Name = "researcher",
                    SystemPrompt = "You are a researcher.",
                    Description = "Researches topics.",
                    WhenToUse = "Use for investigation.",
                    AgentFactory = () => _subAgentMock.Object,
                },
            });

        var loopOptions = new SubAgentOptions
        {
            Templates = new Dictionary<string, SubAgentTemplate>(StringComparer.Ordinal),
            MaxConcurrentSubAgents = 5,
        };

        var registry = new FunctionRegistry();
        await using var loop = new MultiTurnAgentLoop(
            providerAgent.Object,
            registry,
            threadId: "wi77-loop-wiring",
            subAgentOptions: loopOptions,
            subAgentTemplateSource: sharedSource);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var runTask = loop.RunAsync(cts.Token);

        try
        {
            await DrainSingleTurnAsync(loop, "turn one", cts.Token);

            capturedOptions.Should().NotBeEmpty(
                "the loop's parent agent must have been invoked at least once on turn 1");
            var turnOne = capturedOptions[^1]!.Functions!.First(f => f.Name == "Agent");
            turnOne.Description.Should().Contain("researcher");
            turnOne.Description.Should().NotContain("reviewer");

            // Webhook arrives mid-session.
            sharedSource.TryRegister("reviewer", new SubAgentTemplate
            {
                Name = "reviewer",
                SystemPrompt = "You are a reviewer.",
                Description = "Reviews PRs.",
                WhenToUse = "Use after coder lands a change.",
                AgentFactory = () => _subAgentMock.Object,
            }).Should().BeTrue();

            var turnTwoStart = capturedOptions.Count;
            await DrainSingleTurnAsync(loop, "turn two", cts.Token);

            capturedOptions.Count.Should().BeGreaterThan(turnTwoStart,
                "turn 2 must have invoked the parent at least once");
            var turnTwo = capturedOptions[^1]!.Functions!.First(f => f.Name == "Agent");
            turnTwo.Description.Should().Contain("researcher");
            turnTwo.Description.Should().Contain("reviewer");
            turnTwo.Description.Should().Contain("Reviews PRs.");
        }
        finally
        {
            await cts.CancelAsync();
            // Observe the loop task so a cancellation-thrown exception doesn't escape as an
            // unobserved TaskScheduler.UnobservedTaskException flagged by the test host.
            try
            {
                await runTask;
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private static async Task DrainSingleTurnAsync(MultiTurnAgentLoop loop, string text, CancellationToken ct)
    {
        var input = new UserInput([new TextMessage { Text = text, Role = Role.User }]);
        await foreach (var _ in loop.ExecuteRunAsync(input, ct))
        {
            // we don't care about the messages — only about what options the parent agent received,
            // which is captured by the providerAgent mock above. Drain to completion so the run
            // actually finishes before we mutate the source.
        }
    }

    private static async IAsyncEnumerable<IMessage> SingleTextReply(string text)
    {
        await Task.CompletedTask;
        yield return new TextMessage { Text = text, Role = Role.Assistant };
    }

    [Fact]
    public async Task TryRegister_DuplicateOfSeed_DoesNotShadowBuiltIn()
    {
        // Trust-boundary pin: a discovered template whose name collides with a built-in seed
        // must NOT replace the built-in (first-wins). This is the same MergeBuiltInWins
        // semantics, enforced one level deeper at the source itself.
        var added = _source!.TryRegister("researcher", new SubAgentTemplate
        {
            Name = "researcher",
            SystemPrompt = "MALICIOUS",
            Description = "EVIL",
            WhenToUse = "EVIL",
            AgentFactory = () => _subAgentMock.Object,
        });

        added.Should().BeFalse();

        var agentMock = new Mock<IAgent>();
        GenerateReplyOptions? captured = null;
        agentMock
            .Setup(a => a.GenerateReplyAsync(
                It.IsAny<IEnumerable<IMessage>>(),
                It.IsAny<GenerateReplyOptions>(),
                It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<IMessage>, GenerateReplyOptions, CancellationToken>(
                (_, opts, _) => captured = opts)
            .ReturnsAsync([new TextMessage { Text = "ok", Role = Role.Assistant }]);

        _ = await _middleware!.InvokeAsync(
            new MiddlewareContext([new TextMessage { Text = "hi", Role = Role.User }]),
            agentMock.Object);

        var agentFn = captured!.Functions!.First(f => f.Name == "Agent");
        agentFn.Description.Should().Contain("Researches topics.");
        agentFn.Description.Should().NotContain("EVIL");
        agentFn.Description.Should().NotContain("MALICIOUS");
    }
}
