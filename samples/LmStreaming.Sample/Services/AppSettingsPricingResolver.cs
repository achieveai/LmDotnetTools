using AchieveAi.LmDotnetTools.LmCore.Models;

namespace LmStreaming.Sample.Services;

/// <summary>
///     Sample <see cref="IPricingResolver" /> backed by an appsettings <c>Pricing:Models</c> section
///     (model id → per-million prompt / completion rates), so the conversation usage accounting layer can
///     fill an estimated public cost per model (#196). Empty / opt-in by default: a model with no configured
///     rate resolves to <c>null</c> (cost "unavailable"), which is the correct state for flat-rate GitHub
///     Copilot ids that carry no public per-token price. This is a sample convenience for demonstrating the
///     cost pipeline — NOT an authoritative pricing catalog; populate real rates via configuration.
/// </summary>
public sealed class AppSettingsPricingResolver : IPricingResolver
{
    private readonly IReadOnlyDictionary<string, ModelPricing> _pricingByModel;

    /// <summary>Creates a resolver over a case-insensitive snapshot of per-model pricing.</summary>
    /// <param name="pricingByModel">Map of effective model id to its public pricing.</param>
    public AppSettingsPricingResolver(IReadOnlyDictionary<string, ModelPricing> pricingByModel)
    {
        ArgumentNullException.ThrowIfNull(pricingByModel);
        _pricingByModel = pricingByModel;
    }

    /// <summary>
    ///     Builds a resolver from the <c>Pricing:Models</c> configuration section. Each child key is a model
    ///     id whose <c>PromptPerMillion</c> / <c>CompletionPerMillion</c> values are the public rates. Entries
    ///     missing either rate are skipped. An absent section yields an empty resolver (cost unavailable).
    /// </summary>
    /// <param name="configuration">The application configuration.</param>
    public static AppSettingsPricingResolver FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var map = new Dictionary<string, ModelPricing>(StringComparer.OrdinalIgnoreCase);
        foreach (var modelSection in configuration.GetSection("Pricing:Models").GetChildren())
        {
            var prompt = modelSection.GetValue<decimal?>("PromptPerMillion");
            var completion = modelSection.GetValue<decimal?>("CompletionPerMillion");
            if (prompt is null || completion is null)
            {
                continue;
            }

            map[modelSection.Key] = new ModelPricing
            {
                ModelId = modelSection.Key,
                PromptPerMillion = prompt.Value,
                CompletionPerMillion = completion.Value,
                Source = "appsettings:Pricing",
            };
        }

        return new AppSettingsPricingResolver(map);
    }

    /// <inheritdoc />
    public ModelPricing? Resolve(string modelId) =>
        !string.IsNullOrEmpty(modelId) && _pricingByModel.TryGetValue(modelId, out var pricing)
            ? pricing
            : null;
}
