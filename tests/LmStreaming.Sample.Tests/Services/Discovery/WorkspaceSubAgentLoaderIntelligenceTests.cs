using AchieveAi.LmDotnetTools.GithubCopilotProvider.Models;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using LmStreaming.Sample.Services;
using LmStreaming.Sample.Services.Discovery;
using LmStreaming.Sample.Tests.TestDoubles;
using Microsoft.Extensions.Logging;

namespace LmStreaming.Sample.Tests.Services.Discovery;

public sealed class WorkspaceSubAgentLoaderIntelligenceTests
{
    private const string GatewayBaseUrl = "http://localhost:3000";

    [Fact]
    public async Task LoadOneAsync_ResolvesTierLogsParserDiagnosticsAndAttachesFactory()
    {
        var logger = new CapturingLogger<WorkspaceSubAgentLoader>();
        var characteristicsAgentFactory =
            new Func<SubAgentCharacteristics, SubAgentProviderAgent>(_ =>
                new SubAgentProviderAgent(
                    new Mock<IStreamingAgent>().Object,
                    System.Collections.Immutable.ImmutableDictionary<string, object?>.Empty));
        var loader = CreateLoader(logger);
        var item = new SandboxSessionRegistry.DiscoveredItem(
            "subagent",
            "tiered",
            "Tiered agent",
            "/marketplaces/example/tiered.md",
            """
            ---
            name: tiered
            modelintelligence: 3
            effort: invalid
            ---
            Handle the task.
            """);

        var template = await loader.LoadOneWithCharacteristicsAsync(
            new SandboxSession("default", "session", "default", "workspace"),
            item,
            () => new Mock<IStreamingAgent>().Object,
            characteristicsAgentFactory);

        template.Should().NotBeNull();
        template!.DefaultOptions!.ModelId.Should().Be("routable-model");
        template.IsModelExplicitlySelected.Should().BeFalse();
        template.IsModelTierResolved.Should().BeTrue();
        template.CharacteristicsAgentFactory.Should().BeSameAs(characteristicsAgentFactory);
        logger.Entries.Should().ContainSingle(entry =>
            entry.Level == LogLevel.Warning
            && entry.Message.Contains("effort must be one of", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LoadOneAsync_UnresolvedTierRemainsInherited()
    {
        var loader = CreateLoader(
            new CapturingLogger<WorkspaceSubAgentLoader>());
        var item = new SandboxSessionRegistry.DiscoveredItem(
            "subagent",
            "tiered",
            "Tiered agent",
            "/marketplaces/example/tiered.md",
            """
            ---
            name: tiered
            modelintelligence: 99
            ---
            Handle the task.
            """);

        var template = await loader.LoadOneWithCharacteristicsAsync(
            new SandboxSession("default", "session", "default", "workspace"),
            item,
            () => new Mock<IStreamingAgent>().Object,
            _ => throw new InvalidOperationException("Factory should not run while loading."));

        template.Should().NotBeNull();
        template!.DefaultOptions.Should().BeNull();
        template.IsModelExplicitlySelected.Should().BeFalse();
        template.IsModelTierResolved.Should().BeFalse();
    }

    private static WorkspaceSubAgentLoader CreateLoader(
        ILogger<WorkspaceSubAgentLoader> logger)
    {
        var gateway = new SandboxGatewayLifetime(
            new SandboxGatewayOptions { BaseUrl = GatewayBaseUrl },
            NullLogger<SandboxGatewayLifetime>.Instance,
            new HttpClient(new StubHandler()));
        var registry = new SandboxSessionRegistry(
            gateway,
            new SandboxGatewayOptions { BaseUrl = GatewayBaseUrl },
            NullLogger<SandboxSessionRegistry>.Instance,
            new HttpClient(new StubHandler()),
            new AuthOptions(),
            new SessionSecretStore(
                Path.Combine(Path.GetTempPath(), "lmstreaming-test-secrets", Guid.NewGuid().ToString("N")),
                NullLogger<SessionSecretStore>.Instance));
        var catalog = new ProviderRegistry(
            [
                new CopilotModelInfo(
                    "routable-model",
                    "Routable",
                    CopilotModelVendor.OpenAI,
                    CopilotModelTransport.Responses),
            ],
            new Mock<IFileSystemProbe>().Object);
        var resolver = new SubAgentModelResolver(
            catalog,
            new SubAgentIntelligenceOptions
            {
                Tiers = new Dictionary<int, string[]> { [3] = ["routable-model"] },
            },
            new CapturingLogger<SubAgentModelResolver>());

        return new WorkspaceSubAgentLoader(registry, logger, resolver);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("HTTP is not expected");
    }
}
