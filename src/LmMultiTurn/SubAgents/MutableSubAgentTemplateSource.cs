using System.Collections.Concurrent;

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
/// <see cref="Templates"/> returns the live <see cref="ConcurrentDictionary{TKey,TValue}"/>
/// view. Enumerating it during a concurrent <see cref="TryRegister"/> is safe — the
/// snapshot may include or exclude the new entry depending on timing, but never throws
/// and never tears.
/// </para>
/// </remarks>
public sealed class MutableSubAgentTemplateSource
{
    private readonly ConcurrentDictionary<string, SubAgentTemplate> _templates;

    /// <summary>
    /// Creates an empty source.
    /// </summary>
    public MutableSubAgentTemplateSource()
    {
        _templates = new ConcurrentDictionary<string, SubAgentTemplate>(StringComparer.Ordinal);
    }

    /// <summary>
    /// Creates a source pre-seeded with <paramref name="initial"/>. Entries are copied; the
    /// source does not retain a reference to the supplied dictionary.
    /// </summary>
    public MutableSubAgentTemplateSource(IReadOnlyDictionary<string, SubAgentTemplate> initial)
    {
        ArgumentNullException.ThrowIfNull(initial);
        _templates = new ConcurrentDictionary<string, SubAgentTemplate>(initial, StringComparer.Ordinal);
    }

    /// <summary>
    /// Live snapshot of the current templates. Reads are lock-free; consumers should treat
    /// the returned view as read-only.
    /// </summary>
    public IReadOnlyDictionary<string, SubAgentTemplate> Templates => _templates;

    /// <summary>
    /// Atomically registers <paramref name="template"/> under <paramref name="name"/>. Returns
    /// true on first registration and false when an entry already exists for that name.
    /// </summary>
    public bool TryRegister(string name, SubAgentTemplate template)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(template);
        return _templates.TryAdd(name, template);
    }
}
