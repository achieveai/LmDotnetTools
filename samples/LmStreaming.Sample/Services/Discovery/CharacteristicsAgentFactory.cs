using System.Collections.Concurrent;
using System.Collections.Immutable;
using AchieveAi.LmDotnetTools.GithubCopilotProvider.Models;
using AchieveAi.LmDotnetTools.GithubCopilotProvider.Reasoning;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;

namespace LmStreaming.Sample.Services.Discovery;

/// <summary>
/// Creates a transport-correct Copilot agent from final sub-agent spawn characteristics.
/// </summary>
internal sealed class CharacteristicsAgentFactory
{
    private readonly ProviderRegistry _catalog;
    private readonly IStreamingAgent _parentAgent;
    private readonly Func<CopilotModelInfo, IStreamingAgent> _modelAgentFactory;
    private readonly ILogger<CharacteristicsAgentFactory> _logger;
    private readonly CopilotModelInfo? _parentCopilotModel;
    private readonly ImmutableDictionary<string, object?> _parentReasoningExtraProperties;
    private readonly ConcurrentDictionary<string, byte> _warnedFallbacks = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _loggedEffortDiagnostics = new(
        StringComparer.OrdinalIgnoreCase
    );

    public CharacteristicsAgentFactory(
        ProviderRegistry catalog,
        IStreamingAgent parentAgent,
        Func<CopilotModelInfo, IStreamingAgent> modelAgentFactory,
        ILogger<CharacteristicsAgentFactory> logger,
        CopilotModelInfo? parentCopilotModel = null,
        ImmutableDictionary<string, object?>? parentReasoningExtraProperties = null
    )
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(parentAgent);
        ArgumentNullException.ThrowIfNull(modelAgentFactory);
        ArgumentNullException.ThrowIfNull(logger);

        _catalog = catalog;
        _parentAgent = parentAgent;
        _modelAgentFactory = modelAgentFactory;
        _logger = logger;
        _parentCopilotModel = parentCopilotModel;
        _parentReasoningExtraProperties = parentReasoningExtraProperties ?? ImmutableDictionary<string, object?>.Empty;
    }

    /// <summary>
    /// Creates the provider agent and transport-specific reasoning metadata for a spawn.
    /// </summary>
    internal SubAgentProviderAgent Create(SubAgentCharacteristics characteristics)
    {
        ArgumentNullException.ThrowIfNull(characteristics);

        if (!characteristics.IsModelExplicitlySelected && !characteristics.IsModelTierResolved)
        {
            var shaped = _parentCopilotModel is null
                ? new ShapedReasoning(ImmutableDictionary<string, object?>.Empty, null)
                : ShapeReasoning(_parentCopilotModel, characteristics.Effort);
            var extraProperties = shaped.ExtraProperties;
            // Copilot shaping carries no effort for a classic Copilot Anthropic parent (advertises no efforts),
            // a non-Copilot parent (_parentCopilotModel is null), and a template that requests none. The
            // sub-agent reuses the parent model/transport here, so it should inherit the parent's OWN reasoning
            // metadata (e.g. a classic Thinking budget, or the parent's own effort) rather than think with no
            // nudge at all. Testing the effort as well as emptiness matters since shaping an adaptive parent
            // yields the display opt-in alone (#709), which must not shadow the parent's richer metadata.
            if ((extraProperties.IsEmpty || shaped.Effort is null) && !_parentReasoningExtraProperties.IsEmpty)
            {
                extraProperties = _parentReasoningExtraProperties;
            }

            return new SubAgentProviderAgent(_parentAgent, extraProperties) { ShapedEffort = shaped.Effort };
        }

        if (string.IsNullOrWhiteSpace(characteristics.ModelId))
        {
            WarnFallbackOnce("<inherited>", "Sub-agent effective model is null; reusing the parent provider agent");
            return ParentFallback();
        }

        if (!_catalog.TryGetCopilotModel(characteristics.ModelId, out var model))
        {
            WarnFallbackOnce(
                "unknown-explicit-model",
                "Sub-agent effective model {ModelId} is not in the Copilot catalog; "
                    + "reusing the parent provider agent",
                characteristics.ModelId
            );
            return ParentFallback();
        }

        var shapedForModel = ShapeReasoning(model, characteristics.Effort);
        return new SubAgentProviderAgent(_modelAgentFactory(model), shapedForModel.ExtraProperties)
        {
            OwnsAgent = true,
            ShapedEffort = shapedForModel.Effort,
        };
    }

    private SubAgentProviderAgent ParentFallback() =>
        new(_parentAgent, ImmutableDictionary<string, object?>.Empty) { UseParentModel = true };

    private void WarnFallbackOnce(string condition, string message, params object?[] args)
    {
        if (_warnedFallbacks.TryAdd(condition, 0))
        {
            _logger.LogWarning(message, args);
        }
    }

    private ShapedReasoning ShapeReasoning(CopilotModelInfo model, ReasoningEffort? requestedEffort)
    {
        // No effort to shape still shapes something on an adaptive-thinking Anthropic model: its
        // display opt-in is independent of effort, and withholding it returns empty thinking text
        // (#709). Shape decides that; for every other model/transport it returns Empty as before.
        if (requestedEffort is null)
        {
            return new ShapedReasoning(CopilotReasoningShaper.Shape(model, requestedEffort: null), null);
        }

        var selectedEffort = CopilotReasoningShaper.SelectEffort(model, requestedEffort);
        if (selectedEffort is null)
        {
            if (_loggedEffortDiagnostics.TryAdd($"omitted:{model.Id}:{requestedEffort.Value}", 0))
            {
                _logger.LogDebug(
                    "Sub-agent reasoning effort {RequestedEffort} was omitted because Copilot model "
                        + "{ModelId} advertises no supported effort",
                    requestedEffort,
                    model.Id
                );
            }

            return new ShapedReasoning(CopilotReasoningShaper.Shape(model, requestedEffort), null);
        }

        if (
            CopilotReasoningShaper.GetEffortRank(selectedEffort)
                != CopilotReasoningShaper.GetEffortRank(requestedEffort.Value)
            && _loggedEffortDiagnostics.TryAdd($"adjusted:{model.Id}:{requestedEffort.Value}:{selectedEffort}", 0)
        )
        {
            _logger.LogWarning(
                "Sub-agent reasoning effort adjusted from {RequestedEffort} to {SelectedEffort} "
                    + "for Copilot model {ModelId}",
                requestedEffort,
                selectedEffort,
                model.Id
            );
        }

        return new ShapedReasoning(CopilotReasoningShaper.Shape(model, requestedEffort), selectedEffort);
    }

    private sealed record ShapedReasoning(ImmutableDictionary<string, object?> ExtraProperties, string? Effort);
}
