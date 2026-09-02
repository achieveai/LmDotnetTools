using AchieveAi.LmDotnetTools.LmConfig.Capabilities;
using AchieveAi.LmDotnetTools.LmConfig.Models;
using AchieveAi.LmDotnetTools.LmConfig.Services;
using AchieveAi.LmDotnetTools.LmCore.Models;
using Microsoft.Extensions.DependencyInjection;

namespace AchieveAi.LmDotnetTools.LmConfig.Tests.Capabilities;

/// <summary>
///     <see cref="ModelCapacityConfigResolver" /> (#681): the catalog's <c>token_limits</c> answered through
///     LmCore's <see cref="IModelCapacityResolver" />, under every name a model is stamped with, so a
///     per-generation context observation can carry a utilization.
/// </summary>
public class ModelCapacityConfigResolverTests
{
    private static ModelConfig Model(string id, int? window, int maxOutput = 0, params string[] providerNames) =>
        new()
        {
            Id = id,
            IsReasoning = false,
            Capabilities = window is null
                ? null
                : new ModelCapabilities
                {
                    TokenLimits = new TokenLimits { MaxContextTokens = window.Value, MaxOutputTokens = maxOutput },
                },
            Providers =
            [
                .. providerNames.Select(name => new ProviderConfig
                {
                    Name = "p",
                    ModelName = name,
                    Pricing = new PricingConfig { PromptPerMillion = 1, CompletionPerMillion = 1 },
                }),
            ],
        };

    private static AppConfig Catalog(params ModelConfig[] models) => new() { Models = models };

    [Fact]
    public void FromAppConfig_ResolvesTheWindow_ByCatalogIdAndByEveryProviderModelName()
    {
        var resolver = ModelCapacityConfigResolver.FromAppConfig(
            Catalog(Model("gpt-4.1-mini", 1_047_576, 32_768, "openai/gpt-4.1-mini"))
        );

        foreach (var alias in new[] { "gpt-4.1-mini", "openai/gpt-4.1-mini", "GPT-4.1-MINI" })
        {
            var capacity = resolver.Resolve(alias);
            Assert.NotNull(capacity);
            Assert.Equal(1_047_576, capacity!.WindowTokens);
            Assert.Equal(32_768, capacity.MaxOutputTokens);
        }
    }

    [Fact]
    public void FromAppConfig_AModelWithoutLimits_OrWithAZeroWindow_ResolvesNull()
    {
        // A zero window is what the binder leaves when token_limits is omitted; it must read as unknown,
        // never as "the window is zero" — a utilization over zero is not a number anyone can act on.
        var resolver = ModelCapacityConfigResolver.FromAppConfig(
            Catalog(Model("no-caps", null, 0, "vendor/no-caps"), Model("zero", 0, 0, "vendor/zero"))
        );

        Assert.Null(resolver.Resolve("no-caps"));
        Assert.Null(resolver.Resolve("vendor/no-caps"));
        Assert.Null(resolver.Resolve("zero"));
        Assert.Null(resolver.Resolve("no-such-model"));
        Assert.Null(resolver.Resolve(""));
    }

    [Fact]
    public void FromAppConfig_AZeroMaxOutput_ReadsAsUnknown_NotZero()
    {
        var resolver = ModelCapacityConfigResolver.FromAppConfig(Catalog(Model("m", 128_000, 0, "vendor/m")));

        var capacity = resolver.Resolve("m");
        Assert.NotNull(capacity);
        Assert.Equal(128_000, capacity!.WindowTokens);
        Assert.Null(capacity.MaxOutputTokens);
    }

    [Fact]
    public void FromAppConfig_ANameTwoModelsDisagreeOn_IsDropped()
    {
        // Mirrors PricingConfigResolver: a confident wrong window is worse than an absent one.
        var resolver = ModelCapacityConfigResolver.FromAppConfig(
            Catalog(Model("big", 200_000, 8_192, "shared/name"), Model("small", 32_000, 4_096, "shared/name"))
        );

        Assert.Null(resolver.Resolve("shared/name"));
        Assert.Equal(200_000, resolver.Resolve("big")!.WindowTokens);
        Assert.Equal(32_000, resolver.Resolve("small")!.WindowTokens);
    }

    [Fact]
    public void FromAppConfig_ToleratesACatalogBoundWithoutModelsOrProviders()
    {
        var resolver = ModelCapacityConfigResolver.FromAppConfig(new AppConfig { Models = null! });

        Assert.Null(resolver.Resolve("anything"));
    }

    [Fact]
    public void AddLmConfig_RegistersACapacityResolverOverTheRegisteredCatalog()
    {
        var services = new ServiceCollection();
        _ = services.AddLmConfig(Catalog(Model("gpt-4.1-mini", 1_047_576, 32_768, "openai/gpt-4.1-mini")));

        using var provider = services.BuildServiceProvider();
        var resolver = provider.GetService<IModelCapacityResolver>();

        Assert.NotNull(resolver);
        Assert.Equal(1_047_576, resolver!.Resolve("openai/gpt-4.1-mini")!.WindowTokens);
    }

    [Fact]
    public void AddLmConfig_DoesNotDisplaceACapacityResolverTheHostAlreadyRegistered()
    {
        var services = new ServiceCollection();
        _ = services.AddSingleton<IModelCapacityResolver>(new StubCapacityResolver());
        _ = services.AddLmConfig(Catalog(Model("gpt-4.1-mini", 1_047_576, 32_768, "openai/gpt-4.1-mini")));

        using var provider = services.BuildServiceProvider();
        Assert.IsType<StubCapacityResolver>(provider.GetRequiredService<IModelCapacityResolver>());
    }

    private sealed class StubCapacityResolver : IModelCapacityResolver
    {
        public ModelCapacity? Resolve(string modelId) => null;
    }
}
