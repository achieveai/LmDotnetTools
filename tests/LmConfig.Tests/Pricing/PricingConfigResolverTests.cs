using System.Text;
using AchieveAi.LmDotnetTools.LmConfig.Models;
using AchieveAi.LmDotnetTools.LmConfig.Pricing;
using AchieveAi.LmDotnetTools.LmConfig.Services;
using AchieveAi.LmDotnetTools.LmCore.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AchieveAi.LmDotnetTools.LmConfig.Tests.Pricing;

public class PricingConfigResolverTests
{
    private static PricingConfigResolver Resolver() =>
        new(
            new Dictionary<string, PricingConfig>
            {
                ["model-A"] = new() { PromptPerMillion = 2.0, CompletionPerMillion = 8.0 },
            },
            source: "test-catalog",
            version: "2026-07-19"
        );

    [Fact]
    public void Resolve_KnownModel_ReturnsPricingWithProvenance()
    {
        var pricing = Resolver().Resolve("model-A");

        Assert.NotNull(pricing);
        Assert.Equal(2.0m, pricing!.PromptPerMillion);
        Assert.Equal(8.0m, pricing.CompletionPerMillion);
        Assert.Equal("test-catalog", pricing.Source);
        Assert.Equal("2026-07-19", pricing.Version);

        // 1000 input + 500 output at $2/M + $8/M => 6000 micro-units.
        Assert.Equal(6000, pricing.EstimateMicros(1000, 500));
    }

    [Fact]
    public void Resolve_UnknownModel_ReturnsNull()
    {
        Assert.Null(Resolver().Resolve("no-such-model"));
    }

    // --- FromAppConfig: the producer this type never had, which is why nothing ever constructed it and
    // UsageRecord.EstimatedPublicCostMicros was unconditionally null (#328). ---

    private static PricingConfig Rates(double prompt, double completion) =>
        new() { PromptPerMillion = prompt, CompletionPerMillion = completion };

    private static ModelConfig Model(string id, params ProviderConfig[] providers) =>
        new()
        {
            Id = id,
            IsReasoning = false,
            Providers = providers,
        };

    private static ProviderConfig Provider(
        string name,
        string modelName,
        PricingConfig pricing,
        params SubProviderConfig[] subProviders
    ) =>
        new()
        {
            Name = name,
            ModelName = modelName,
            Pricing = pricing,
            SubProviders = subProviders.Length == 0 ? null : subProviders,
        };

    private static AppConfig Catalog(params ModelConfig[] models) => new() { Models = models };

    [Fact]
    public void FromAppConfig_SingleProvider_ResolvesByCatalogIdAndByProviderModelName()
    {
        var resolver = PricingConfigResolver.FromAppConfig(
            Catalog(Model("gpt-4.1-mini", Provider("OpenAI", "openai/gpt-4.1-mini", Rates(0.4, 1.6)))),
            source: "test-catalog",
            version: "2026-08-24"
        );

        // A usage record is stamped with EffectiveModelId, which is the catalog id or the provider's own
        // model name depending on which layer resolved it, so both names have to answer.
        foreach (var alias in new[] { "gpt-4.1-mini", "openai/gpt-4.1-mini" })
        {
            var pricing = resolver.Resolve(alias);
            Assert.NotNull(pricing);
            Assert.Equal(0.4m, pricing!.PromptPerMillion);
            Assert.Equal(1.6m, pricing.CompletionPerMillion);
            Assert.Equal("test-catalog", pricing.Source);
            Assert.Equal("2026-08-24", pricing.Version);
        }
    }

    [Fact]
    public void FromAppConfig_SubProviderRates_ResolveUnderTheSubProviderName()
    {
        var resolver = PricingConfigResolver.FromAppConfig(
            Catalog(
                Model(
                    "big-model",
                    Provider(
                        "OpenRouter",
                        "vendor/big-model",
                        Rates(3.0, 15.0),
                        new SubProviderConfig
                        {
                            Name = "Together",
                            ModelName = "together/big-model",
                            Priority = 1,
                            Pricing = Rates(2.5, 12.0),
                        }
                    )
                )
            )
        );

        // Mirrors ProviderResolution.EffectivePricing: a request routed through the sub-provider is billed
        // at the sub-provider's rates, so that is what its name must resolve to.
        var sub = resolver.Resolve("together/big-model");
        Assert.NotNull(sub);
        Assert.Equal(2.5m, sub!.PromptPerMillion);
        Assert.Equal(12.0m, sub.CompletionPerMillion);

        // The parent provider's own name keeps the parent's rates.
        var parent = resolver.Resolve("vendor/big-model");
        Assert.NotNull(parent);
        Assert.Equal(3.0m, parent!.PromptPerMillion);
        Assert.Equal(15.0m, parent.CompletionPerMillion);
    }

    [Fact]
    public void FromAppConfig_NamePricedTwoWays_ResolvesNullRatherThanPickingOne()
    {
        // A usage record carries a model name and never the provider that served it. With two providers at
        // different rates there is no basis to choose, so the shared catalog id goes dark rather than
        // return a figure that gets summed and believed downstream. A silently WRONG cost is worse than a
        // silently null one, which is the judgement call at the centre of #328.
        var resolver = PricingConfigResolver.FromAppConfig(
            Catalog(
                Model(
                    "contested",
                    Provider("Cheap", "cheap/contested", Rates(1.0, 2.0)),
                    Provider("Pricey", "pricey/contested", Rates(9.0, 20.0))
                )
            )
        );

        Assert.Null(resolver.Resolve("contested"));

        // Each provider's own distinct name is still unambiguous, so those keep resolving — the rule drops
        // the ambiguous name only, not the whole model.
        Assert.Equal(1.0m, resolver.Resolve("cheap/contested")!.PromptPerMillion);
        Assert.Equal(9.0m, resolver.Resolve("pricey/contested")!.PromptPerMillion);
    }

    [Fact]
    public void FromAppConfig_NamePricedIdenticallyByEveryProvider_StillResolves()
    {
        // Agreement is not ambiguity. Dropping this case would discard most of a real catalog, where one
        // model is routinely listed at the same published price under several providers.
        var resolver = PricingConfigResolver.FromAppConfig(
            Catalog(
                Model(
                    "agreed",
                    Provider("A", "a/agreed", Rates(3.0, 15.0)),
                    Provider("B", "b/agreed", Rates(3.0, 15.0))
                )
            )
        );

        var pricing = resolver.Resolve("agreed");
        Assert.NotNull(pricing);
        Assert.Equal(3.0m, pricing!.PromptPerMillion);
        Assert.Equal(15.0m, pricing.CompletionPerMillion);
    }

    [Fact]
    public void FromAppConfig_LooksUpCaseInsensitively()
    {
        var resolver = PricingConfigResolver.FromAppConfig(
            Catalog(Model("Claude-Sonnet-4-5", Provider("Anthropic", "claude-sonnet-4-5", Rates(3.0, 15.0))))
        );

        Assert.NotNull(resolver.Resolve("CLAUDE-SONNET-4-5"));
    }

    [Fact]
    public void FromAppConfig_UnknownModel_ResolvesNull()
    {
        var resolver = PricingConfigResolver.FromAppConfig(
            Catalog(Model("known", Provider("P", "p/known", Rates(1.0, 2.0))))
        );

        Assert.Null(resolver.Resolve("no-such-model"));
    }

    // --- The registration half of the gap: LmConfig's own composition root never registered an
    // IPricingResolver, so MultiTurnAgentLoop's optional pricingResolver had nothing to bind to. ---

    [Fact]
    public void AddLmConfig_RegistersAPricingResolverOverTheRegisteredCatalog()
    {
        var services = new ServiceCollection();
        _ = services.AddLmConfig(
            Catalog(Model("gpt-4.1-mini", Provider("OpenAI", "openai/gpt-4.1-mini", Rates(0.4, 1.6))))
        );

        using var provider = services.BuildServiceProvider();
        var resolver = provider.GetService<IPricingResolver>();

        Assert.NotNull(resolver);
        var pricing = resolver!.Resolve("gpt-4.1-mini");
        Assert.NotNull(pricing);
        Assert.Equal(0.4m, pricing!.PromptPerMillion);
        Assert.Equal("lmconfig:AppConfig", pricing.Source);

        // 1000 input + 500 output at $0.40/M + $1.60/M => 400 + 800 = 1200 micro-units. This is the figure
        // that now lands on UsageRecord.EstimatedPublicCostMicros where it was previously always null.
        Assert.Equal(1200, pricing.EstimateMicros(1000, 500));
    }

    [Fact]
    public void AddLmConfig_DoesNotDisplaceAPricingResolverTheHostAlreadyRegistered()
    {
        // A host with an authoritative catalog of its own must keep it: TryAdd, not Add.
        var services = new ServiceCollection();
        _ = services.AddSingleton<IPricingResolver>(new StubPricingResolver());
        _ = services.AddLmConfig(
            Catalog(Model("gpt-4.1-mini", Provider("OpenAI", "openai/gpt-4.1-mini", Rates(0.4, 1.6))))
        );

        using var provider = services.BuildServiceProvider();
        Assert.IsType<StubPricingResolver>(provider.GetRequiredService<IPricingResolver>());
    }

    // --- The IConfiguration binder path. Microsoft.Extensions.Configuration.Binder does not enforce
    // `required` (System.Text.Json does, which is why the JSON entry points never see this), so the
    // documented entry point — AddLmConfig(Configuration.GetSection("LmConfig")), see
    // src/LmConfig/docs/Configuration-Loading-Guide.md — binds a provider that declares no `pricing` with
    // Pricing null. No test exercised this path at all, which is how a bare NullReferenceException thrown
    // out of the registration lambda survived review. ---

    private static IConfigurationSection LmConfigSection(string json) =>
        new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json)))
            .Build()
            .GetSection("LmConfig");

    [Fact]
    public void AddLmConfig_BoundFromConfiguration_ProviderWithoutPricing_ResolvesNullRatherThanThrowing()
    {
        var services = new ServiceCollection();
        _ = services.AddLmConfig(
            LmConfigSection(
                """
                {
                  "LmConfig": {
                    "Models": [
                      {
                        "Id": "m1",
                        "IsReasoning": false,
                        "Providers": [ { "Name": "P", "ModelName": "p/m1" } ]
                      }
                    ]
                  }
                }
                """
            )
        );

        using var provider = services.BuildServiceProvider();

        // Before the fix this threw NullReferenceException from inside the registration lambda, so a host
        // that merely resolved the resolver fell over on a catalog that bound fine on the merge base.
        var resolver = provider.GetRequiredService<IPricingResolver>();

        // A route that declared no rate resolves to nothing. Not to zero, and not to a borrowed number.
        Assert.Null(resolver.Resolve("m1"));
        Assert.Null(resolver.Resolve("p/m1"));
    }

    [Fact]
    public void AddLmConfig_BoundFromConfiguration_OneProviderPricedOneNot_DropsTheSharedAlias()
    {
        var services = new ServiceCollection();
        _ = services.AddLmConfig(
            LmConfigSection(
                """
                {
                  "LmConfig": {
                    "Models": [
                      {
                        "Id": "mixed",
                        "IsReasoning": false,
                        "Providers": [
                          {
                            "Name": "A",
                            "ModelName": "a/mixed",
                            "Pricing": { "PromptPerMillion": 3.0, "CompletionPerMillion": 15.0 }
                          },
                          { "Name": "B", "ModelName": "b/mixed" }
                        ]
                      }
                    ]
                  }
                }
                """
            )
        );

        using var provider = services.BuildServiceProvider();
        var resolver = provider.GetRequiredService<IPricingResolver>();

        // The catalog id answers for both routes, and one of them published no rate. Reporting A's rate
        // for it would be a confident number for a request that may have been billed at B's — the exact
        // wrong-number failure the conflict rule exists to avoid — so the shared alias goes dark.
        Assert.Null(resolver.Resolve("mixed"));

        // B's own name is dropped for the same reason.
        Assert.Null(resolver.Resolve("b/mixed"));

        // A's own name names exactly one route, and that route published a rate, so it still resolves.
        var priced = resolver.Resolve("a/mixed");
        Assert.NotNull(priced);
        Assert.Equal(3.0m, priced!.PromptPerMillion);
        Assert.Equal(15.0m, priced.CompletionPerMillion);
    }

    private sealed class StubPricingResolver : IPricingResolver
    {
        public ModelPricing? Resolve(string modelId) => null;
    }
}
