using System.Collections.Immutable;
using AchieveAi.LmDotnetTools.LmCore.Agents;

namespace AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;

/// <summary>
/// Thread-safe, mutable catalog of sub-agent templates. Crosses the boundary between
/// webhook threads that register newly-discovered templates mid-session and the agent
/// loop that reads them when building each turn's tool descriptors.
/// </summary>
/// <remarks>
/// <para>
/// Collision policy is <b>first-wins</b>: <see cref="TryRegister"/> only adds new entries
/// and returns false on conflict. This matches the trust-boundary semantics established
/// by <c>WorkspaceSubAgentLoader.MergeBuiltInWins</c> — built-in (trusted) seeds enter
/// the source first, and discovered (untrusted) entries cannot shadow them.
/// </para>
/// <para>
/// <see cref="Templates"/> returns the immutable snapshot current at the time of the read.
/// A previously returned snapshot remains unchanged when templates are registered or rebound.
/// </para>
/// </remarks>
public sealed class MutableSubAgentTemplateSource
{
    private readonly object _updateGate = new();
    private ImmutableDictionary<string, SubAgentTemplate> _templates;
    private Func<IStreamingAgent>? _agentFactory;
    private Func<SubAgentCharacteristics, SubAgentProviderAgent>? _characteristicsAgentFactory;
    private bool _rebindRegistrations;

    /// <summary>
    /// Creates an empty source.
    /// </summary>
    public MutableSubAgentTemplateSource()
    {
        _templates = ImmutableDictionary.Create<string, SubAgentTemplate>(StringComparer.Ordinal);
    }

    /// <summary>
    /// Creates a source pre-seeded with <paramref name="initial"/>. Entries are copied; the
    /// source does not retain a reference to the supplied dictionary.
    /// </summary>
    public MutableSubAgentTemplateSource(IReadOnlyDictionary<string, SubAgentTemplate> initial)
    {
        ArgumentNullException.ThrowIfNull(initial);
        _templates = ImmutableDictionary.CreateRange(StringComparer.Ordinal, initial);
    }

    /// <summary>
    /// Immutable snapshot of the current templates. Reads are lock-free.
    /// </summary>
    public IReadOnlyDictionary<string, SubAgentTemplate> Templates => Volatile.Read(ref _templates);

    /// <summary>
    /// Atomically registers <paramref name="template"/> under <paramref name="name"/>. Returns
    /// true on first registration and false when an entry already exists for that name.
    /// </summary>
    public bool TryRegister(string name, SubAgentTemplate template)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(template);

        lock (_updateGate)
        {
            var current = Volatile.Read(ref _templates);
            if (current.ContainsKey(name))
            {
                return false;
            }

            Volatile.Write(ref _templates, current.Add(name, RebindTemplate(template)));
            return true;
        }
    }

    /// <summary>
    /// Atomically replaces each retained template with a copy using the latest provider factories.
    /// Templates registered after this operation are rebound to the same factories before they are
    /// published.
    /// </summary>
    public void RebindFactories(
        Func<IStreamingAgent> agentFactory,
        Func<SubAgentCharacteristics, SubAgentProviderAgent>? characteristicsAgentFactory
    )
    {
        ArgumentNullException.ThrowIfNull(agentFactory);

        lock (_updateGate)
        {
            _agentFactory = agentFactory;
            _characteristicsAgentFactory = characteristicsAgentFactory;
            _rebindRegistrations = true;

            var current = Volatile.Read(ref _templates);
            var rebound = current.ToBuilder();
            foreach (var entry in current)
            {
                rebound[entry.Key] = RebindTemplate(entry.Value);
            }

            Volatile.Write(ref _templates, rebound.ToImmutable());
        }
    }

    private SubAgentTemplate RebindTemplate(SubAgentTemplate template)
    {
        if (!_rebindRegistrations)
        {
            return template;
        }

        var inheritedAgentFactory = template.AgentFactory;
        return template with
        {
            CharacteristicsAgentFactory = _characteristicsAgentFactory is null
                ? null
                : characteristics =>
                {
                    var provider = _characteristicsAgentFactory(characteristics);
                    return characteristics.IsModelExplicitlySelected
                        || characteristics.IsModelTierResolved
                        ? provider
                        : provider with
                        {
                            Agent = inheritedAgentFactory(),
                            OwnsAgent = true,
                        };
                },
        };
    }
}
