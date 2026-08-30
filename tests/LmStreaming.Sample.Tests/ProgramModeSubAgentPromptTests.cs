using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using LmStreaming.Sample.Services;
using LmStreaming.Sample.Services.Discovery;

namespace LmStreaming.Sample.Tests;

/// <summary>
/// Spawn-path assembly tests for the per-mode sub-agent prompt fragment (#610). The child's system
/// prompt is read from <c>SubAgentTemplate.SystemPrompt</c> in exactly one place
/// (<c>SubAgentManager</c>'s spawn path), so asserting on the templates the composition root
/// produces IS asserting on the prompt every spawned sub-agent runs with.
/// </summary>
public sealed class ProgramModeSubAgentPromptTests
{
    private const string Fragment = "Mode-wide sub-agent expectations.";

    private static AgentProfile Profile(string? fragment, string? placement) =>
        new("mode-1", "Mode One", "primary prompt") { SubAgentPrompt = fragment, SubAgentPromptPlacement = placement };

    /// <summary>
    /// Runs the REAL composition-root path (non-test provider branch, no sandbox) so deleting the
    /// fold call in <c>BuildSubAgentOptionsAsync</c> — not just breaking the helper — turns this red.
    /// </summary>
    private static async Task<SubAgentOptions?> BuildAsync(AgentProfile mode) =>
        await global::Program.BuildSubAgentOptionsAsync(
            isTestMode: false,
            testAgentBuilder: Mock.Of<LmStreaming.Sample.Services.ITestAgentBuilder>(),
            loggerFactory: NullLoggerFactory.Instance,
            providerAgentFactory: () => Mock.Of<IStreamingAgent>(),
            characteristicsAgentFactory: _ => throw new InvalidOperationException("not spawned here"),
            sandboxSession: null,
            workspaceLoader: null!,
            marketplaceLoader: null!,
            workspaceStore: null!,
            logger: NullLogger.Instance,
            mode: mode
        );

    [Fact]
    public async Task Append_FoldsFragmentAfterEveryTemplatePrompt_WithBlankLineSeparator()
    {
        var baseline = BuiltInSubAgentTemplates.Create(() => Mock.Of<IStreamingAgent>());

        var options = await BuildAsync(Profile(Fragment, "append"));

        options.Should().NotBeNull();
        options!.Templates.Keys.Should().BeEquivalentTo(baseline.Keys);
        foreach (var (key, template) in options.Templates)
        {
            template.SystemPrompt.Should().Be($"{baseline[key].SystemPrompt}\n\n{Fragment}");
        }
    }

    [Fact]
    public async Task Prepend_FoldsFragmentBeforeEveryTemplatePrompt_WithBlankLineSeparator()
    {
        var baseline = BuiltInSubAgentTemplates.Create(() => Mock.Of<IStreamingAgent>());

        var options = await BuildAsync(Profile(Fragment, "prepend"));

        options.Should().NotBeNull();
        foreach (var (key, template) in options!.Templates)
        {
            template.SystemPrompt.Should().Be($"{Fragment}\n\n{baseline[key].SystemPrompt}");
        }
    }

    [Fact]
    public async Task AbsentPlacement_DefaultsToAppend()
    {
        var baseline = BuiltInSubAgentTemplates.Create(() => Mock.Of<IStreamingAgent>());

        var options = await BuildAsync(Profile(Fragment, placement: null));

        foreach (var (key, template) in options!.Templates)
        {
            template.SystemPrompt.Should().Be($"{baseline[key].SystemPrompt}\n\n{Fragment}");
        }
    }

    [Fact]
    public async Task AbsentFragment_LeavesEveryTemplatePromptByteForByte()
    {
        var baseline = BuiltInSubAgentTemplates.Create(() => Mock.Of<IStreamingAgent>());

        var options = await BuildAsync(Profile(fragment: null, placement: null));

        options.Should().NotBeNull();
        foreach (var (key, template) in options!.Templates)
        {
            // Byte-for-byte: no separator, no empty-string append.
            template.SystemPrompt.Should().Be(baseline[key].SystemPrompt);
        }
    }

    [Fact]
    public async Task WhitespaceFragment_IsTreatedAsAbsent()
    {
        var baseline = BuiltInSubAgentTemplates.Create(() => Mock.Of<IStreamingAgent>());

        var options = await BuildAsync(Profile(fragment: "   ", placement: "prepend"));

        foreach (var (key, template) in options!.Templates)
        {
            template.SystemPrompt.Should().Be(baseline[key].SystemPrompt);
        }
    }

    /// <summary>
    /// The fragment must survive the workflow-controller hop: the controller catalog is rebuilt
    /// from the conversation's enriched catalog with the agent factories reset (the
    /// characteristics factory is deliberately DROPPED there — which is exactly why the fragment
    /// travels in the template's SystemPrompt and not via CharacteristicsAgentFactory).
    /// </summary>
    [Fact]
    public async Task WorkflowControllerHop_PreservesTheFoldedPrompt()
    {
        var options = await BuildAsync(Profile(Fragment, "prepend"));

        var controllerTemplates = BuiltInSubAgentTemplates.CreateWorkflowControllerTemplates(
            options!.Templates,
            () => Mock.Of<IStreamingAgent>()
        );

        controllerTemplates.Keys.Should().BeEquivalentTo(options.Templates.Keys);
        foreach (var (key, template) in controllerTemplates)
        {
            template.SystemPrompt.Should().StartWith($"{Fragment}\n\n");
            template.SystemPrompt.Should().Be(options.Templates[key].SystemPrompt);
            template.CharacteristicsAgentFactory.Should().BeNull("the hop drops the characteristics factory");
        }
    }

    /// <summary>
    /// The fold must cover BOTH exits of <c>BuildSubAgentOptionsAsync</c> — this drives the
    /// sandbox/enrichment exit (both loaders are best-effort and yield nothing against the
    /// stubbed gateway), so deleting the fold on that exit alone cannot go green while the
    /// no-sandbox tests above stay green.
    /// </summary>
    [Fact]
    public async Task Append_FoldsOnTheEnrichedSandboxExitToo()
    {
        var baseline = BuiltInSubAgentTemplates.Create(() => Mock.Of<IStreamingAgent>());
        await using var registry = CreateRegistry();
        var workspaceLoader = new WorkspaceSubAgentLoader(registry, NullLogger<WorkspaceSubAgentLoader>.Instance);
        var marketplaceLoader = new MarketplaceSubAgentLoader(
            new ThrowingMarketplaceCatalogClient(),
            NullLogger<MarketplaceSubAgentLoader>.Instance
        );

        var options = await global::Program.BuildSubAgentOptionsAsync(
            isTestMode: false,
            testAgentBuilder: Mock.Of<LmStreaming.Sample.Services.ITestAgentBuilder>(),
            loggerFactory: NullLoggerFactory.Instance,
            providerAgentFactory: () => Mock.Of<IStreamingAgent>(),
            characteristicsAgentFactory: _ => throw new InvalidOperationException("not spawned here"),
            sandboxSession: new SandboxSession("default", "session", "default", "workspace"),
            workspaceLoader: workspaceLoader,
            marketplaceLoader: marketplaceLoader,
            workspaceStore: Mock.Of<LmStreaming.Sample.Persistence.IWorkspaceStore>(),
            logger: NullLogger.Instance,
            mode: Profile(Fragment, "append")
        );

        options.Should().NotBeNull();
        options!.Templates.Keys.Should().BeEquivalentTo(baseline.Keys);
        foreach (var (key, template) in options.Templates)
        {
            template.SystemPrompt.Should().Be($"{baseline[key].SystemPrompt}\n\n{Fragment}");
        }
    }

    /// <summary>
    /// PR #613 F-001: a mode switch rebuilds the agent and rebinds the conversation's shared
    /// template source with a freshly folded seed. The rebind must REPLACE seed-keyed template
    /// content (the seed is the conversation's own trusted, freshly built catalog) — otherwise the
    /// first mode's fragment is pinned for the life of the conversation. Y-and-no-X: the new
    /// spawn catalog carries mode B's fragment and none of mode A's.
    /// </summary>
    [Fact]
    public async Task ModeSwitchRebind_RefreshesSeedTemplatesToTheNewModesFragment()
    {
        const string FragmentA = "Mode A fragment: be terse.";
        const string FragmentB = "Mode B fragment: be thorough.";
        await using var registry = CreateRegistry();
        Func<SubAgentCharacteristics, SubAgentProviderAgent> charFactory = _ =>
            throw new InvalidOperationException("not spawned here");

        var optionsA = await BuildAsync(Profile(FragmentA, "append"));
        var bindingA = global::Program.BindConversationSubAgents(
            registry,
            "session",
            "conversation",
            optionsA!.Templates,
            () => Mock.Of<IStreamingAgent>(),
            charFactory
        );

        // A webhook discovery lands between the two builds; its key is NOT in any seed.
        bindingA
            .Source.TryRegister(
                "webhook_discovered",
                new SubAgentTemplate
                {
                    Name = "Webhook",
                    SystemPrompt = "Webhook prompt.",
                    AgentFactory = () => Mock.Of<IStreamingAgent>(),
                }
            )
            .Should()
            .BeTrue();

        // Mode switch: the pool factory rebuilds and rebinds with mode B's freshly folded catalog.
        var optionsB = await BuildAsync(Profile(FragmentB, "append"));
        var rebound = global::Program.BindConversationSubAgents(
            registry,
            "session",
            "conversation",
            optionsB!.Templates,
            () => Mock.Of<IStreamingAgent>(),
            charFactory
        );

        rebound.Source.Should().BeSameAs(bindingA.Source, "the conversation keeps one template source");
        foreach (var key in optionsB.Templates.Keys)
        {
            var prompt = rebound.Source.Templates[key].SystemPrompt;
            prompt.Should().Contain(FragmentB, $"template '{key}' must carry the CURRENT mode's fragment");
            prompt.Should().NotContain(FragmentA, $"template '{key}' must not leak the previous mode's fragment");
        }

        // The webhook-registered template's key is absent from the new seed: it survives untouched.
        rebound.Source.Templates["webhook_discovered"].SystemPrompt.Should().Be("Webhook prompt.");
        rebound.Source.Templates["webhook_discovered"].Name.Should().Be("Webhook");
    }

    /// <summary>
    /// The leak variant of the same defect: switching to a mode with NO fragment must drop the old
    /// mode's fragment entirely — the rebind seed is unfolded, so the catalog returns to the
    /// built-in prompts byte-for-byte.
    /// </summary>
    [Fact]
    public async Task ModeSwitchRebind_ToAFragmentlessMode_DropsTheOldModesFragment()
    {
        const string FragmentA = "Mode A fragment: be terse.";
        var baseline = BuiltInSubAgentTemplates.Create(() => Mock.Of<IStreamingAgent>());
        await using var registry = CreateRegistry();
        Func<SubAgentCharacteristics, SubAgentProviderAgent> charFactory = _ =>
            throw new InvalidOperationException("not spawned here");

        var optionsA = await BuildAsync(Profile(FragmentA, "prepend"));
        _ = global::Program.BindConversationSubAgents(
            registry,
            "session",
            "conversation",
            optionsA!.Templates,
            () => Mock.Of<IStreamingAgent>(),
            charFactory
        );

        var optionsB = await BuildAsync(Profile(fragment: null, placement: null));
        var rebound = global::Program.BindConversationSubAgents(
            registry,
            "session",
            "conversation",
            optionsB!.Templates,
            () => Mock.Of<IStreamingAgent>(),
            charFactory
        );

        foreach (var (key, template) in rebound.Source.Templates)
        {
            template.SystemPrompt.Should().Be(baseline[key].SystemPrompt, "no trace of mode A's fragment may remain");
        }
    }

    [Fact]
    public void ToAgentProfile_CarriesFragmentAndPlacement()
    {
        var mode = new ChatMode
        {
            Id = "m",
            Name = "M",
            SystemPrompt = "p",
            SubAgentPrompt = Fragment,
            SubAgentPromptPlacement = "prepend",
        };

        var profile = mode.ToAgentProfile();

        profile.SubAgentPrompt.Should().Be(Fragment);
        profile.SubAgentPromptPlacement.Should().Be("prepend");
    }

    [Fact]
    public void ToAgentProfile_LeavesAbsentFieldsNull()
    {
        var mode = new ChatMode
        {
            Id = "m",
            Name = "M",
            SystemPrompt = "p",
        };

        var profile = mode.ToAgentProfile();

        profile.SubAgentPrompt.Should().BeNull();
        profile.SubAgentPromptPlacement.Should().BeNull();
    }

    private static SandboxSessionRegistry CreateRegistry()
    {
        const string baseUrl = "http://localhost:3000";
        var options = new SandboxGatewayOptions { BaseUrl = baseUrl };
        var gateway = new SandboxGatewayLifetime(
            options,
            NullLogger<SandboxGatewayLifetime>.Instance,
            new HttpClient(new StubHandler())
        );
        return new SandboxSessionRegistry(
            gateway,
            options,
            NullLogger<SandboxSessionRegistry>.Instance,
            new HttpClient(new StubHandler()),
            new AuthOptions(),
            new SessionSecretStore(
                Path.Combine(Path.GetTempPath(), "lmstreaming-test-secrets", Guid.NewGuid().ToString("N")),
                NullLogger<SessionSecretStore>.Instance
            )
        );
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) => throw new InvalidOperationException("HTTP is not expected to succeed in this test");
    }

    /// <summary>
    /// Marketplace catalog client whose failure exercises the loader's best-effort catch, so
    /// enrichment contributes nothing and the built-in catalog is what the fold applies to.
    /// </summary>
    private sealed class ThrowingMarketplaceCatalogClient : IMarketplaceCatalogClient
    {
        public Task<MarketplaceCatalog> GetCatalogAsync(
            IReadOnlyList<string>? marketplaces = null,
            CancellationToken ct = default
        ) => throw new MarketplaceCatalogUnavailableException("gateway offline in this test");
    }
}
