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
    private readonly ConcurrentDictionary<string, byte> _warnedFallbacks = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _loggedEffortDiagnostics = new(
        StringComparer.OrdinalIgnoreCase
    );

    public CharacteristicsAgentFactory(
        ProviderRegistry catalog,
        IStreamingAgent parentAgent,
        Func<CopilotModelInfo, IStreamingAgent> modelAgentFactory,
        ILogger<CharacteristicsAgentFactory> logger,
        CopilotModelInfo? parentCopilotModel = null
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
    }

    /// <summary>
    /// Creates the provider agent and transport-specific reasoning metadata for a spawn.
    /// </summary>
    internal SubAgentProviderAgent Create(SubAgentCharacteristics characteristics)
    {
        ArgumentNullException.ThrowIfNull(characteristics);

        if (!characteristics.IsModelExplicitlySelected && !characteristics.IsModelTierResolved)
        {
            var extraProperties = _parentCopilotModel is null
                ? ImmutableDictionary<string, object?>.Empty
                : ShapeReasoning(_parentCopilotModel, characteristics.Effort);
            return new SubAgentProviderAgent(_parentAgent, extraProperties);
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

        return new SubAgentProviderAgent(_modelAgentFactory(model), ShapeReasoning(model, characteristics.Effort))
        {
            OwnsAgent = true,
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

    private ImmutableDictionary<string, object?> ShapeReasoning(
        CopilotModelInfo model,
        ReasoningEffort? requestedEffort
    )
    {
        if (requestedEffort is null)
        {
            return ImmutableDictionary<string, object?>.Empty;
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

            return ImmutableDictionary<string, object?>.Empty;
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

        return CopilotReasoningShaper.Shape(model, requestedEffort);
    }
}
