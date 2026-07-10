using System.Collections.Immutable;
using AchieveAi.LmDotnetTools.AnthropicProvider.Models;
using AchieveAi.LmDotnetTools.GithubCopilotProvider.Models;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.OpenAiResponsesProvider.Models;

namespace AchieveAi.LmDotnetTools.GithubCopilotProvider.Reasoning;

/// <summary>
/// Shapes a typed reasoning request into transport-specific Copilot request metadata.
/// </summary>
public static class CopilotReasoningShaper
{
    private static readonly string[] S_selectableEfforts = ["none", "minimal", "low", "medium", "high", "xhigh"];

    /// <summary>
    /// Selects an advertised effort and returns request extra properties for the model's transport.
    /// </summary>
    public static ImmutableDictionary<string, object?> Shape(CopilotModelInfo model, ReasoningEffort? requestedEffort)
    {
        ArgumentNullException.ThrowIfNull(model);

        var effort = SelectEffort(model, requestedEffort);
        if (effort is null)
        {
            return ImmutableDictionary<string, object?>.Empty;
        }

        return model.Transport switch
        {
            CopilotModelTransport.Anthropic => ImmutableDictionary<string, object?>.Empty.Add(
                "OutputConfig",
                new AnthropicOutputConfig { Effort = effort }
            ),
            CopilotModelTransport.Responses => ImmutableDictionary<string, object?>.Empty.Add(
                "Reasoning",
                new ResponseReasoningOptions { Effort = effort }
            ),
            _ => ImmutableDictionary<string, object?>.Empty,
        };
    }

    /// <summary>
    /// Selects the greatest advertised effort not exceeding the request, or the lowest selectable
    /// advertised effort when none are at or below the request.
    /// </summary>
    /// <returns>
    /// The selected canonical effort, or <see langword="null"/> when no effort was requested or the
    /// model advertises no selectable effort.
    /// </returns>
    public static string? SelectEffort(CopilotModelInfo model, ReasoningEffort? requestedEffort)
    {
        ArgumentNullException.ThrowIfNull(model);

        if (requestedEffort is null)
        {
            return null;
        }

        var advertisedRanks = model
            .ReasoningEfforts.Select(GetSelectableRank)
            .Where(rank => rank >= 0)
            .Distinct()
            .ToArray();
        if (advertisedRanks.Length == 0)
        {
            return null;
        }

        var requestedRank = requestedEffort.Value switch
        {
            ReasoningEffort.Low => 2,
            ReasoningEffort.Medium => 3,
            ReasoningEffort.High => 4,
            ReasoningEffort.Xhigh => 5,
            _ => throw new ArgumentOutOfRangeException(nameof(requestedEffort)),
        };
        var selectedRank = advertisedRanks
            .Where(rank => rank <= requestedRank)
            .DefaultIfEmpty(advertisedRanks.Min())
            .Max();
        return S_selectableEfforts[selectedRank];
    }

    private static int GetSelectableRank(string effort)
    {
        for (var rank = 0; rank < S_selectableEfforts.Length; rank++)
        {
            if (string.Equals(effort?.Trim(), S_selectableEfforts[rank], StringComparison.OrdinalIgnoreCase))
            {
                return rank;
            }
        }

        return -1;
    }
}
