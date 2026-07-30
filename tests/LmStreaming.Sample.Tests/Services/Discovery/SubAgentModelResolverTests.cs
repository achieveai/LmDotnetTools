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

    [Fact]
    public void ResolveClimbing_ExplicitModelWinsOverTier()
    {
        var resolver = CreateResolver(
            new Dictionary<int, string[]> { [3] = ["catalog-model"] },
            Model("catalog-model", CopilotModelTransport.Responses)
        );

        resolver.ResolveClimbing("explicit-model", 3).Should().Be("explicit-model");
    }

    [Fact]
    public void ResolveClimbing_NullTierInheritsParent()
    {
        var resolver = CreateResolver(
            new Dictionary<int, string[]> { [3] = ["catalog-model"] },
            Model("catalog-model", CopilotModelTransport.Responses)
        );

        resolver.ResolveClimbing(null, null).Should().BeNull();
    }

    [Fact]
    public void ResolveClimbing_RequestedTierPresent_UsesItWithoutClimbing()
    {
        var resolver = CreateResolver(
            new Dictionary<int, string[]> { [1] = ["cheap-model"], [3] = ["strong-model"] },
            Model("cheap-model", CopilotModelTransport.Responses),
            Model("strong-model", CopilotModelTransport.Responses)
        );

        resolver.ResolveClimbing(null, 1).Should().Be("cheap-model");
    }

    [Fact]
    public void ResolveClimbing_RequestedTierUnconfigured_ClimbsToNextHigherConfiguredTier()
    {
        // The requested tier (2) is unmapped in this deployment; climb UP to the nearest
        // configured tier (3) rather than silently inheriting the parent — this is the behaviour
        // the single-tier Resolve lacks (Resolve(null, 2) returns null here).
        var resolver = CreateResolver(
            new Dictionary<int, string[]> { [3] = ["strong-model"] },
            Model("strong-model", CopilotModelTransport.Responses)
        );

        resolver.Resolve(null, 2).Should().BeNull();
        resolver.ResolveClimbing(null, 2).Should().Be("strong-model");
    }

    [Fact]
    public void ResolveClimbing_RequestedTierUnroutable_ClimbsPastItToNextRoutableTier()
    {
        // Tier 1 is configured but its only candidate is not in the catalog (unroutable); the climb
        // skips it and resolves the next-higher routable tier.
        var resolver = CreateResolver(
            new Dictionary<int, string[]> { [1] = ["missing-model"], [3] = ["strong-model"] },
            Model("strong-model", CopilotModelTransport.Responses)
        );

        resolver.ResolveClimbing(null, 1).Should().Be("strong-model");
    }

    [Fact]
    public void ResolveClimbing_FromZeroResolvesLowestAvailableTier()
    {
        // The "lowest available tier" entry point (start at 0, climb up) reused by the JSON-repair
        // path: pick the cheapest configured, routable model regardless of which low tiers are mapped.
        var resolver = CreateResolver(
            new Dictionary<int, string[]> { [1] = ["cheap-model"], [3] = ["strong-model"] },
            Model("cheap-model", CopilotModelTransport.Responses),
            Model("strong-model", CopilotModelTransport.Responses)
        );

        resolver.ResolveClimbing(null, 0).Should().Be("cheap-model");
    }

    [Fact]
    public void ResolveClimbing_NoRoutableTierAtOrAboveRequested_ReturnsNullAndWarnsOnce()
    {
        var logger = new CapturingLogger<SubAgentModelResolver>();
        var resolver = CreateResolver(
            new SubAgentIntelligenceOptions { Tiers = new Dictionary<int, string[]> { [1] = ["cheap-model"] } },
            logger,
            Model("cheap-model", CopilotModelTransport.Responses)
        );

        resolver.ResolveClimbing(null, 3).Should().BeNull();
        resolver.ResolveClimbing(null, 3).Should().BeNull();

        logger.Entries.Count(entry => entry.Level == LogLevel.Warning).Should().Be(1);
    }

    [Fact]
    public void IsKnownModel_TrueOnlyForTierSanctionedRoutableCatalogModel()
    {
        // The runtime guard behind the Agent tool's free-form `model` override is scoped to the
        // TIER-CONFIGURED allowed set, NOT the whole catalog. A tier-sanctioned routable model passes
        // (case-insensitively); a real catalog id that no tier sanctions (gpt-5.4), an unroutable tier
        // candidate, an invented id, a subagent_type, a placeholder, and null all fail so the manager
        // drops them — the sub-agent inherits the parent/tier model instead of burning tokens on a model
        // the deployment never sanctioned.
        var resolver = CreateResolver(
            new Dictionary<int, string[]>
            {
                [1] = ["gpt-5.6-luna", "unsupported-model"],
                [5] = ["claude-opus-5"],
            },
            Model("gpt-5.6-luna", CopilotModelTransport.Responses),
            Model("claude-opus-5", CopilotModelTransport.Anthropic),
            Model("gpt-5.4", CopilotModelTransport.Responses), // real catalog id sanctioned by NO tier
            Model("unsupported-model", CopilotModelTransport.Unsupported) // tier candidate, but unroutable
        );

        resolver.IsKnownModel("gpt-5.6-luna").Should().BeTrue();
        resolver.IsKnownModel("GPT-5.6-LUNA").Should().BeTrue("catalog lookup is case-insensitive");
        resolver.IsKnownModel("claude-opus-5").Should().BeTrue();
        resolver.IsKnownModel("gpt-5.4").Should().BeFalse("a real catalog id no tier sanctions is out of scope");
        resolver.IsKnownModel("unsupported-model").Should().BeFalse("an unroutable tier candidate is not selectable");
        resolver.IsKnownModel("gpt-5").Should().BeFalse("an invented id is not in the catalog");
        resolver.IsKnownModel("general-purpose").Should().BeFalse("a subagent_type is not a model id");
        resolver.IsKnownModel("none").Should().BeFalse("a placeholder is not a model id");
        resolver.IsKnownModel(null).Should().BeFalse();
        resolver.IsKnownModel("   ").Should().BeFalse();
    }

    [Fact]
    public void AvailableModelIds_ReturnsTierSanctionedRoutableIdsOnly()
    {
        var resolver = CreateResolver(
            new Dictionary<int, string[]>
            {
                [1] = ["gpt-5.6-luna", "unsupported-model"],
                [5] = ["claude-opus-5"],
            },
            Model("gpt-5.6-luna", CopilotModelTransport.Responses),
            Model("claude-opus-5", CopilotModelTransport.Anthropic),
            Model("gpt-5.4", CopilotModelTransport.Responses),
            Model("unsupported-model", CopilotModelTransport.Unsupported)
        );

        // Only the tier-configured, routable ids are advertised — the full catalog (gpt-5.4) and the
        // unroutable tier candidate are excluded.
        resolver.AvailableModelIds.Should().BeEquivalentTo(["gpt-5.6-luna", "claude-opus-5"]);
    }

    [Fact]
    public void AvailableModelIds_DedupesAcrossTiersAndCanonicalizesCasing()
    {
        var resolver = CreateResolver(
            new Dictionary<int, string[]>
            {
                [1] = ["gpt-5.6-luna"],
                [3] = ["GPT-5.6-LUNA", "claude-sonnet-5"],
            },
            Model("gpt-5.6-luna", CopilotModelTransport.Responses),
            Model("claude-sonnet-5", CopilotModelTransport.Anthropic)
        );

        resolver.AvailableModelIds.Should().BeEquivalentTo(["gpt-5.6-luna", "claude-sonnet-5"]);
        resolver.AvailableModelIds.Should().OnlyHaveUniqueItems("a model configured in several tiers is advertised once");
    }

    [Fact]
    public void EmptyTiers_DisablesOverrides_NoAvailableIdsAndNothingKnown()
    {
        // With no tiers configured there is no sanctioned set, so overrides are disabled entirely: nothing
        // is advertised and every override is dropped (the sub-agent inherits the parent model), even a
        // real catalog id.
        var resolver = CreateResolver([], Model("gpt-5.6-luna", CopilotModelTransport.Responses));

        resolver.AvailableModelIds.Should().BeEmpty();
        resolver
            .IsKnownModel("gpt-5.6-luna")
            .Should()
            .BeFalse("no tier sanctions it, so even a real catalog id is dropped");
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
