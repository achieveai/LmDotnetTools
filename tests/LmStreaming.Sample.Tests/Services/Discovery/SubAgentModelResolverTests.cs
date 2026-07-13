using AchieveAi.LmDotnetTools.GithubCopilotProvider.Models;
using LmStreaming.Sample.Services;
using LmStreaming.Sample.Services.Discovery;
using LmStreaming.Sample.Tests.TestDoubles;
using Microsoft.Extensions.Logging;

namespace LmStreaming.Sample.Tests.Services.Discovery;

public sealed class SubAgentModelResolverTests
{
    [Fact]
    public void Resolve_ExplicitModelWinsOverTier()
    {
        var resolver = CreateResolver(
            new Dictionary<int, string[]> { [3] = ["catalog-model"] },
            Model("catalog-model", CopilotModelTransport.Responses)
        );

        var resolved = resolver.Resolve("explicit-model", 3);

        resolved.Should().Be("explicit-model");
    }

    [Theory]
    [InlineData("inherit")]
    [InlineData(" InHeRiT ")]
    public void Resolve_InheritModelUsesTier(string inheritModel)
    {
        var resolver = CreateResolver(
            new Dictionary<int, string[]> { [3] = ["catalog-model"] },
            Model("catalog-model", CopilotModelTransport.Responses)
        );

        var resolved = resolver.Resolve(inheritModel, 3);

        resolved.Should().Be("catalog-model");
    }

    [Fact]
    public void Resolve_ExplicitModelAndTierLogsIgnoredTierOncePerTier()
    {
        var logger = new CapturingLogger<SubAgentModelResolver>();
        var resolver = CreateResolver(new SubAgentIntelligenceOptions(), logger);

        resolver.Resolve(" explicit-model ", 3).Should().Be("explicit-model");
        resolver.Resolve("EXPLICIT-MODEL", 3).Should().Be("EXPLICIT-MODEL");
        resolver.Resolve("other-model", 3).Should().Be("other-model");
        resolver.Resolve("explicit-model", 4).Should().Be("explicit-model");

        var notices = logger.Entries.Where(entry => entry.Level == LogLevel.Information).ToArray();
        notices.Should().HaveCount(2);
        notices
            .Should()
            .Contain(entry =>
                entry.Message.Contains("explicit-model")
                && entry.Message.Contains("3")
                && entry.Message.Contains("ignored")
            );
        notices
            .Should()
            .Contain(entry =>
                entry.Message.Contains("explicit-model")
                && entry.Message.Contains("4")
                && entry.Message.Contains("ignored")
            );
    }

    [Fact]
    public void Resolve_UsesFirstRoutableCatalogCandidate()
    {
        var resolver = CreateResolver(
            new Dictionary<int, string[]>
            {
                [3] = ["missing-model", "unsupported-model", "anthropic-model", "responses-model"],
            },
            Model("unsupported-model", CopilotModelTransport.Unsupported),
            Model("anthropic-model", CopilotModelTransport.Anthropic),
            Model("responses-model", CopilotModelTransport.Responses)
        );

        var resolved = resolver.Resolve(null, 3);

        resolved.Should().Be("anthropic-model");
    }

    [Theory]
    [InlineData(ResolutionFailure.EmptyMap)]
    [InlineData(ResolutionFailure.MissingTier)]
    [InlineData(ResolutionFailure.UnroutableCandidates)]
    public void Resolve_UnresolvedTierReturnsNullAndWarnsOnce(ResolutionFailure failure)
    {
        var logger = new CapturingLogger<SubAgentModelResolver>();
        var options = failure switch
        {
            ResolutionFailure.EmptyMap => new SubAgentIntelligenceOptions(),
            ResolutionFailure.MissingTier => new SubAgentIntelligenceOptions
            {
                Tiers = new Dictionary<int, string[]> { [2] = ["responses-model"] },
            },
            ResolutionFailure.UnroutableCandidates => new SubAgentIntelligenceOptions
            {
                Tiers = new Dictionary<int, string[]> { [3] = ["missing-model", "unsupported-model"] },
            },
            _ => throw new ArgumentOutOfRangeException(nameof(failure)),
        };
        var resolver = CreateResolver(
            options,
            logger,
            Model("unsupported-model", CopilotModelTransport.Unsupported),
            Model("responses-model", CopilotModelTransport.Responses)
        );

        resolver.Resolve(null, 3).Should().BeNull();
        resolver.Resolve(null, 3).Should().BeNull();

        logger.Entries.Count(entry => entry.Level == LogLevel.Warning).Should().Be(1);
    }

    private static SubAgentModelResolver CreateResolver(
        Dictionary<int, string[]> tiers,
        params CopilotModelInfo[] models
    ) =>
        CreateResolver(
            new SubAgentIntelligenceOptions { Tiers = tiers },
            new CapturingLogger<SubAgentModelResolver>(),
            models
        );

    private static SubAgentModelResolver CreateResolver(
        SubAgentIntelligenceOptions options,
        ILogger<SubAgentModelResolver> logger,
        params CopilotModelInfo[] models
    )
    {
        var registry = new ProviderRegistry(models, new Mock<IFileSystemProbe>().Object);
        return new SubAgentModelResolver(registry, options, logger);
    }

    private static CopilotModelInfo Model(string id, CopilotModelTransport transport) =>
        new(id, id, CopilotModelVendor.OpenAI, transport);

    public enum ResolutionFailure
    {
        EmptyMap,
        MissingTier,
        UnroutableCandidates,
    }
}
