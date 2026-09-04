using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using LmStreaming.Sample.Services;

namespace LmStreaming.Sample.Tests;

/// <summary>
/// Typed review publication is a PARENT-ONLY surface (spec §6). These tests drive the real path —
/// registry → <c>Program.RegisterReviewPublicationTools</c> → real <see cref="MultiTurnAgentLoop"/> →
/// real <see cref="SubAgentManager"/> spawn — rather than stopping at the exclusion list, because the
/// list only matters if the loop actually applies it.
/// </summary>
public sealed class ReviewPublicationParentOnlyExposureTests
{
    private static readonly IReadOnlyList<string> PublicationToolNames = ReviewPublicationFunctionProvider.ToolNames;

    // --- Registration gate ---------------------------------------------------------------------

    [Fact]
    public void WithoutABridge_NothingIsRegisteredAndNothingIsExcluded()
    {
        var registry = new FunctionRegistry();
        _ = registry.AddFunction(Contract("SafeTool"), OkHandler(), "ParentTools");
        var options = NoTemplates(["StartWorkflow"]);

        var result = global::Program.RegisterReviewPublicationTools(registry, options, bridge: null);

        RegisteredNames(registry).Should().NotIntersectWith(PublicationToolNames);
        result!.NonInheritedToolNames.Should().BeEquivalentTo(["StartWorkflow"]);
    }

    /// <summary>
    /// Every reason a deployment or a thread is not authorized has to produce the SAME no-surface
    /// outcome, and it has to produce it by yielding no bridge at all.
    /// </summary>
    [Theory]
    [InlineData(0, "https://daemon.test", "secret")] // no review scope on the thread
    [InlineData(7, null, "secret")] // no configured bridge url
    [InlineData(7, "https://daemon.test", " ")] // no configured secret
    [InlineData(7, "not-a-url", "secret")] // unusable bridge url
    [InlineData(7, "file:///etc/passwd", "secret")] // non-http scheme
    public void BridgeIsOnlyBuiltWhenTheThreadIsScopedAndTheDeploymentIsConfigured(
        long roundId,
        string? bridgeUrl,
        string? secret
    )
    {
        var scope =
            roundId == 0
                ? null
                : ReviewPublicationScope.TryCreate(
                    new ReviewPublicationScopeRequest
                    {
                        RoundId = roundId,
                        Provider = "github",
                        RepoId = 1234,
                        PrId = "118",
                        ExpectedHeadSha = "abc123",
                    }
                );

        global::Program.TryBuildReviewPublicationBridge(scope, bridgeUrl, secret).Should().BeNull();
    }

    [Fact]
    public void WithABridge_AllSixNamesAreRegisteredAndUnionedIntoTheExistingExclusions()
    {
        var registry = new FunctionRegistry();
        var options = NoTemplates(["StartWorkflow", "GetAgentTranscript"]);

        var result = global::Program.RegisterReviewPublicationTools(registry, options, new StubBridge());

        RegisteredNames(registry).Should().Contain(PublicationToolNames);
        result!
            .NonInheritedToolNames.Should()
            .BeEquivalentTo([.. PublicationToolNames, "StartWorkflow", "GetAgentTranscript"]);
    }

    // --- The real loop → spawn path --------------------------------------------------------------

    /// <summary>
    /// The one that matters: under the SHIPPED <c>code-review-daemon</c> mode, a child spawned with
    /// <c>add_tools: ["*"]</c> — the strongest grant a spawn can ask for, which expands to every tool
    /// the parent exposes — still sees none of the six, while the parent keeps all six.
    /// </summary>
    [Fact]
    public async Task ChildSpawnedWithAddToolsWildcard_UnderTheShippedMode_SeesNoneOfTheSix()
    {
        await using var loop = BuildParentLoop();

        loop.RegisteredToolNames.Should().Contain(PublicationToolNames);

        var child = await SpawnChildAsync(loop, "reviewer", addTools: ["*"]);

        child.RegisteredToolNames.Should().NotIntersectWith(PublicationToolNames);
        // Non-vacuity: the same wildcard spawn DID inherit the parent's ordinary domain tool, so the
        // absence above is the exclusion working rather than the spawn inheriting nothing at all.
        child.RegisteredToolNames.Should().Contain("SafeTool");
    }

    /// <summary>
    /// A spawn that names the publication tools outright — the obvious escalation attempt — gets them
    /// no more than the wildcard does, because the intersection is against the already-filtered parent
    /// contracts.
    /// </summary>
    [Fact]
    public async Task ChildSpawnedNamingThePublicationToolsExplicitly_StillSeesNoneOfTheSix()
    {
        await using var loop = BuildParentLoop();

        var child = await SpawnChildAsync(loop, "reviewer", addTools: [.. PublicationToolNames]);

        child.RegisteredToolNames.Should().NotIntersectWith(PublicationToolNames);
    }

    /// <summary>
    /// The mode's <c>subAgentRequiredTools</c> union (#623) is applied to every spawn AFTER its
    /// template restriction. It must not be a second door into the publication surface, so a
    /// configuration that demands the six still cannot deliver them.
    /// </summary>
    [Fact]
    public async Task ModeRequiredTools_CannotReintroduceThePublicationSurface()
    {
        await using var loop = BuildParentLoop(requiredToolNames: [.. PublicationToolNames, "SafeTool"]);

        // The "restricted" template enables NOTHING, so every name the child ends up with arrived via
        // the required-tools union — which is exactly the door under test.
        var child = await SpawnChildAsync(loop, "restricted", addTools: []);

        child.RegisteredToolNames.Should().NotIntersectWith(PublicationToolNames);
        // Non-vacuity: the union IS live on this spawn — it delivered SafeTool through the same path
        // that was asked to deliver the six.
        child.RegisteredToolNames.Should().Contain("SafeTool");
    }

    /// <summary>
    /// The workflow transparency seam. A WorkflowAgent controller runs on its OWN isolated registry and
    /// inherits the launching conversation's tools through
    /// <see cref="SubAgentOptions.ExternalInheritableTools"/> — the one path by which a name can enter a
    /// loop that never registered it. The six must not survive it, at any depth below.
    /// </summary>
    /// <remarks>
    /// The snapshot's <c>Contracts</c> are filtered; its <c>Handlers</c> map is the parent's UNFILTERED
    /// one. That is inert rather than exploitable — <c>MultiTurnAgentLoop</c> merges by iterating
    /// <c>Contracts</c> and only then looks a handler up, so a handler with no contract is never merged
    /// and never dispatchable. The assertion below is therefore on the contracts and on the delegate's
    /// actual surface, not on the handler map: asserting the map would be asserting an implementation
    /// detail this seam does not read.
    /// </remarks>
    [Fact]
    public async Task TheWorkflowTransparencyMerge_CannotCarryThePublicationToolsIntoADelegate()
    {
        await using var parent = BuildParentLoop();
        var snapshot = parent.SubAgentManager!.GetInheritableToolSnapshot();

        snapshot.Contracts.Select(c => c.Name).Should().NotIntersectWith(PublicationToolNames);

        // A controller on its own workflow-only registry, inheriting the conversation's tools.
        var controllerRegistry = new FunctionRegistry();
        _ = controllerRegistry.AddFunction(Contract("WorkflowControlTool"), OkHandler(), "WorkflowTools");
        await using var controller = new MultiTurnAgentLoop(
            Mock.Of<IStreamingAgent>(),
            controllerRegistry,
            threadId: "review-publication-workflow",
            subAgentOptions: new SubAgentOptions
            {
                Templates = new Dictionary<string, SubAgentTemplate> { ["reviewer"] = Template(enabledTools: null) },
                MaxConcurrentSubAgents = 5,
                ExternalInheritableTools = snapshot,
            }
        );

        var delegateAgent = await SpawnChildAsync(controller, "reviewer", addTools: ["*"]);

        delegateAgent.RegisteredToolNames.Should().NotIntersectWith(PublicationToolNames);
        // Non-vacuity: the transparency merge IS live on this delegate — it carried the conversation's
        // ordinary domain tool across the same seam that was asked to carry the six.
        delegateAgent.RegisteredToolNames.Should().Contain("SafeTool");
    }

    /// <summary>
    /// A descendant that somehow guessed a name still cannot reach the daemon: the tool is absent from
    /// its surface, so the call is refused by the loop rather than forwarded.
    /// </summary>
    [Fact]
    public async Task AChildCannotInvokeAPublicationToolAtAll()
    {
        var bridge = new StubBridge();
        await using var loop = BuildParentLoop(bridge);

        var child = await SpawnChildAsync(loop, "reviewer", addTools: ["*"]);

        child.RegisteredToolNames.Should().NotContain("CreateRootSummary");
        bridge.Calls.Should().BeEmpty();
    }

    // --- Harness ----------------------------------------------------------------------------------

    /// <summary>
    /// A parent conversation wired the way <c>Program.cs</c> wires one, under the SHIPPED
    /// <c>code-review-daemon</c> mode: the mode travels the real seam
    /// (yaml → <see cref="SystemChatModes"/> → <c>ToAgentProfile</c> → <c>ApplyModeRequiredTools</c>),
    /// the publication tools are registered through the real composition-root seam, and the resulting
    /// exclusions are carried into the loop that takes the inheritable snapshot.
    /// </summary>
    private static MultiTurnAgentLoop BuildParentLoop(
        IReviewPublicationBridge? bridge = null,
        IReadOnlyCollection<string>? requiredToolNames = null
    )
    {
        var mode = SystemChatModes.GetById(SystemChatModes.CodeReviewDaemonModeId)!.ToAgentProfile();

        var registry = new FunctionRegistry();
        _ = registry.AddFunction(Contract("SafeTool"), OkHandler(), "ParentTools");

        var built = new SubAgentOptions
        {
            Templates = new Dictionary<string, SubAgentTemplate>
            {
                // Inherit-all: without the exclusion this template WOULD inherit the six.
                ["reviewer"] = Template(enabledTools: null),
                // Restricted to nothing, so anything it ends up with came from RequiredToolNames.
                ["restricted"] = Template(enabledTools: []),
            },
            MaxConcurrentSubAgents = 5,
            NonInheritedToolNames = ["StartWorkflow"],
        };
        built = global::Program.ApplyModeRequiredTools(built, mode, NullLogger.Instance);
        if (requiredToolNames is not null)
        {
            built = built with { RequiredToolNames = requiredToolNames };
        }

        var options = global::Program.RegisterReviewPublicationTools(registry, built, bridge ?? new StubBridge())!;

        return new MultiTurnAgentLoop(
            Mock.Of<IStreamingAgent>(),
            registry,
            threadId: "review-publication-exposure",
            subAgentOptions: options
        );
    }

    private static SubAgentTemplate Template(IReadOnlyList<string>? enabledTools) =>
        new()
        {
            SystemPrompt = "You are a review-dimension worker.",
            EnabledTools = enabledTools,
            AgentFactory = () => Mock.Of<IStreamingAgent>(),
        };

    /// <summary>Options for the registration-gate tests, which never spawn anything.</summary>
    private static SubAgentOptions NoTemplates(IReadOnlyCollection<string> nonInherited) =>
        new() { Templates = new Dictionary<string, SubAgentTemplate>(), NonInheritedToolNames = nonInherited };

    private static IEnumerable<string> RegisteredNames(FunctionRegistry registry)
    {
        var (contracts, _) = registry.Build();
        return [.. contracts.Select(c => c.Name)];
    }

    private static async Task<MultiTurnAgentLoop> SpawnChildAsync(
        MultiTurnAgentLoop parent,
        string template,
        string[] addTools
    )
    {
        var receipt = await parent.SubAgentManager!.SpawnAsync(
            template,
            "inspect the diff",
            runInBackground: true,
            addTools: addTools
        );
        using var doc = JsonDocument.Parse(receipt);
        var agentId = doc.RootElement.GetProperty("agent_id").GetString()!;
        parent.SubAgentManager.TryGetAgent(agentId, out var agent).Should().BeTrue();
        return agent.Should().BeOfType<MultiTurnAgentLoop>().Subject;
    }

    private static FunctionContract Contract(string name) =>
        new()
        {
            Name = name,
            Description = name,
            Parameters = [],
        };

    private static ToolHandler OkHandler() =>
        (_, _, _) => Task.FromResult<ToolHandlerResult>(ToolHandlerResult.FromText("ok"));

    /// <summary>Records every forwarded action so a test can prove none was forwarded.</summary>
    private sealed class StubBridge : IReviewPublicationBridge
    {
        public List<string> Calls { get; } = [];

        public Task<ReviewPublicationResult> SendAsync(
            string operation,
            JsonElement arguments,
            CancellationToken cancellationToken
        )
        {
            Calls.Add(operation);
            return Task.FromResult(new ReviewPublicationResult(true, "{}", null));
        }
    }
}
