using System.Collections.Immutable;
using AchieveAi.LmDotnetTools.LmCore.Agents;

namespace AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;

/// <summary>
/// Provider agent and provider-specific request properties produced for a sub-agent spawn.
/// </summary>
public sealed record SubAgentProviderAgent
{
    private IStreamingAgent _agent = null!;
    private ImmutableDictionary<string, object?> _extraProperties = null!;

    /// <summary>
    /// Initializes a provider-agent routing result.
    /// </summary>
    /// <param name="Agent">The provider agent selected for the spawn.</param>
    /// <param name="ExtraProperties">Provider request hints to merge into the spawn options.</param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="Agent"/> or <paramref name="ExtraProperties"/> is null.
    /// </exception>
    public SubAgentProviderAgent(
        IStreamingAgent Agent,
        ImmutableDictionary<string, object?> ExtraProperties)
    {
        ArgumentNullException.ThrowIfNull(Agent);
        ArgumentNullException.ThrowIfNull(ExtraProperties);

        this.Agent = Agent;
        this.ExtraProperties = ExtraProperties;
    }

    /// <summary>The provider agent selected for the spawn.</summary>
    public IStreamingAgent Agent
    {
        get => _agent;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            _agent = value;
        }
    }

    /// <summary>Opaque provider request hints merged into the spawn's request options.</summary>
    public ImmutableDictionary<string, object?> ExtraProperties
    {
        get => _extraProperties;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            _extraProperties = value;
        }
    }

    /// <summary>
    /// Whether request options should restore the parent model id. This is used when routing falls
    /// back from an unsupported explicit model to the parent provider agent.
    /// </summary>
    public bool UseParentModel { get; init; }

    /// <summary>Deconstructs the result into its compatibility-preserving positional values.</summary>
    /// <param name="agent">Receives <see cref="Agent"/>.</param>
    /// <param name="extraProperties">Receives <see cref="ExtraProperties"/>.</param>
    public void Deconstruct(
        out IStreamingAgent agent,
        out ImmutableDictionary<string, object?> extraProperties)
    {
        agent = Agent;
        extraProperties = ExtraProperties;
    }
}
