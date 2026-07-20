namespace LmStreaming.Sample.Services;

/// <summary>
/// A single model exposed by an Anthropic-compatible provider family (e.g. DeepSeek), discovered
/// from env vars rather than a curated catalog entry. <see cref="AnthropicCompatProviders"/> is the
/// only place these are constructed.
/// </summary>
/// <param name="Id">Slugified dropdown/provider id, e.g. <c>deepseek-v4-pro</c>.</param>
/// <param name="DisplayName">Human-facing label; currently identical to <see cref="ModelName"/>.</param>
/// <param name="ModelName">The exact wire model id sent to the Anthropic-compatible API.</param>
/// <param name="BaseUrl">The family's Anthropic-compatible base URL (e.g. <c>https://api.deepseek.com/anthropic</c>).</param>
/// <param name="ApiKey">The family's API key.</param>
/// <param name="FamilyKey">The env-var key identifying the family, e.g. <c>DEEPSEEK</c>. Used for the dropdown group label.</param>
public sealed record AnthropicCompatModel(
    string Id,
    string DisplayName,
    string ModelName,
    string BaseUrl,
    string ApiKey,
    string FamilyKey);

/// <summary>
/// Discovers Anthropic-compatible provider families from env vars, so adding a new model or a whole
/// new vendor is a <c>.env</c> edit rather than a code change. See <c>ANTHROPIC_COMPAT_PROVIDERS</c>
/// in <c>.env.example</c> for the scheme.
/// </summary>
internal static class AnthropicCompatProviders
{
    /// <summary>
    /// Reads <c>ANTHROPIC_COMPAT_PROVIDERS</c> (comma-separated family keys) and, for each key,
    /// <c>{KEY}_ANTHROPIC_URL</c> / <c>{KEY}_APIKEY</c> / <c>{KEY}_MODELS</c> (comma-separated model
    /// names). A family missing any of its three vars is skipped and logged rather than throwing,
    /// mirroring the Copilot discovery path's "degrade to empty list" contract.
    /// </summary>
    public static IReadOnlyList<AnthropicCompatModel> DiscoverFromEnv(ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);

        var logger = loggerFactory.CreateLogger("LmStreaming.Sample.AnthropicCompatProviders");
        var familyKeysRaw = Environment.GetEnvironmentVariable("ANTHROPIC_COMPAT_PROVIDERS");
        if (string.IsNullOrWhiteSpace(familyKeysRaw))
        {
            return [];
        }

        var models = new List<AnthropicCompatModel>();
        var familyKeys = familyKeysRaw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        // The generated id is the dropdown/provider key. ProviderRegistry indexes models by id with an
        // OrdinalIgnoreCase comparer and would SILENTLY overwrite/skip a later entry sharing an id — so
        // two model names that slugify to the same id (e.g. same-family "kimi-2.5" vs "kimi-2-5", or the
        // same model name configured under two families) would make a configured model vanish with no
        // signal. Track ids as they are minted (same comparer as the registry) and skip+warn on a
        // collision so the loss is loud and deterministic (first configured entry wins) rather than a
        // silent, order-dependent drop.
        var seenIds = new Dictionary<string, AnthropicCompatModel>(StringComparer.OrdinalIgnoreCase);

        foreach (var familyKey in familyKeys)
        {
            var baseUrl = Environment.GetEnvironmentVariable($"{familyKey}_ANTHROPIC_URL");
            var apiKey = Environment.GetEnvironmentVariable($"{familyKey}_APIKEY");
            var modelNamesRaw = Environment.GetEnvironmentVariable($"{familyKey}_MODELS");

            if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(modelNamesRaw))
            {
                logger.LogWarning(
                    "Skipping Anthropic-compatible family {FamilyKey}: one or more of its _ANTHROPIC_URL/_APIKEY/_MODELS env vars is not set.",
                    familyKey
                );
                continue;
            }

            var modelNames = modelNamesRaw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            foreach (var modelName in modelNames)
            {
                var id = Slugify(modelName);
                if (seenIds.TryGetValue(id, out var existing))
                {
                    logger.LogWarning(
                        "Skipping Anthropic-compatible model {ModelName} (family {FamilyKey}): its generated id {Id} "
                            + "collides with already-registered model {ExistingModelName} (family {ExistingFamilyKey}). "
                            + "Model ids must be unique; rename one of the colliding models so their slugified ids differ.",
                        modelName,
                        familyKey,
                        id,
                        existing.ModelName,
                        existing.FamilyKey
                    );
                    continue;
                }

                var model = new AnthropicCompatModel(id, modelName, modelName, baseUrl, apiKey, familyKey);
                seenIds[id] = model;
                models.Add(model);
            }
        }

        return models;
    }

    private static string Slugify(string modelName)
    {
        var chars = modelName.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray();
        return new string(chars);
    }
}
