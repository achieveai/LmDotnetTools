using System.Text.RegularExpressions;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using AchieveAi.LmDotnetTools.Sandbox;
using Microsoft.Data.Sqlite;

namespace AchieveAi.LmDotnetTools.LmAgentInfra.Capabilities;

/// <summary>One searchable capability: a skill, a sub-agent template or a plugin.</summary>
/// <param name="Key">
/// Stable identity. Skills: <c>skill:&lt;path&gt;</c> (a skill's path is unique per sandbox). Agents:
/// <c>agent:&lt;template key&gt;</c>. Plugins: <c>plugin:&lt;marketplace&gt;/&lt;plugin&gt;</c>.
/// </param>
/// <param name="Kind">
/// The kind searched by (<see cref="CapabilityCatalog.SkillKind"/>, <see cref="CapabilityCatalog.AgentKind"/>,
/// <see cref="CapabilityCatalog.PluginKind"/>).
/// </param>
/// <param name="Name">
/// What the agent calls it by: a skill's gateway name (plugin-qualified for marketplace skills), an agent's
/// template key (its <c>subagent_type</c>), a plugin's name.
/// </param>
/// <param name="Plugin">The owning plugin; <c>null</c> for a workspace skill or an agent whose key names none.</param>
/// <param name="Marketplace">
/// The owning marketplace alias; <c>null</c> for a workspace skill, and for every agent (a template key does
/// not carry its marketplace).
/// </param>
/// <param name="Description">The description shown to the agent.</param>
public sealed record CapabilityItem(
    string Key,
    string Kind,
    string Name,
    string? Plugin,
    string? Marketplace,
    string Description
);

/// <summary>Why a configured selector loaded nothing.</summary>
public enum SelectorProblem
{
    /// <summary>Not <c>&lt;kind&gt;:&lt;ref&gt;</c>, or a kind this slice does not support.</summary>
    Malformed,

    /// <summary>No catalog item matches the ref.</summary>
    Unknown,

    /// <summary>The ref matches more than one item; <see cref="SelectorIssue.Candidates"/> lists them.</summary>
    Ambiguous,
}

/// <summary>A selector that resolved to nothing, and the names that could have been meant.</summary>
public sealed record SelectorIssue(string Selector, SelectorProblem Problem, IReadOnlyList<string> Candidates);

/// <summary>
/// An in-memory full-text index over one sandbox's capabilities (SQLite FTS5), plus exact name/path
/// resolution. Owns its only SQLite connection: every command and <see cref="Dispose"/> run under one
/// lock, so concurrent callers (root and sub-agents) are safe and the connection is never shared.
/// </summary>
/// <remarks>
/// <para>
/// <b>One FTS5 table and one typo vocabulary per kind</b>, in the same database. A search ranks only its
/// kind's rows, against that kind's BM25 statistics and words, so adding agents or plugins never moves a
/// skill result. One shared table cost skill recall@5 0.905 → 0.881 on the eval set.
/// </para>
/// <para>
/// <b>Ranking</b> (tuned against the slice-1 search eval set): each query word outside a stop list
/// becomes one quoted FTS5 phrase, ORed, so no query text is ever parsed as FTS5 syntax. Words of four or
/// more characters match as stem prefixes (<c>"failing"*</c> also finds <c>failures</c>). BM25 weights:
/// name 1 + name words 2 (so a name token counts 3), plugin 1, marketplace 0.5, description 1. A query
/// equal to an item's name, or to its last <c>:</c>-segment, ranks first; a query that is a prefix of
/// one ranks next.
/// </para>
/// <para>
/// <b>Typos:</b> when the first pass finds fewer hits than asked for, each query word that is not a
/// prefix of any indexed word is replaced by its closest indexed words (trigram Dice ≥ 0.5, via a
/// trigram FTS5 table over the vocabulary) and the search runs once more. Only short-of-hits queries
/// take this path: correcting every unknown word added noise and lowered recall on the eval set.
/// </para>
/// </remarks>
public sealed partial class CapabilityCatalog : IDisposable
{
    /// <summary>The kind of every skill item.</summary>
    public const string SkillKind = "skill";

    /// <summary>The key prefix of every skill item; the rest of the key is the skill's path.</summary>
    public const string SkillKeyPrefix = "skill:";

    /// <summary>The kind of every sub-agent template item.</summary>
    public const string AgentKind = "agent";

    /// <summary>The key prefix of every agent item; the rest of the key is the template key.</summary>
    public const string AgentKeyPrefix = "agent:";

    /// <summary>The kind of every plugin item.</summary>
    public const string PluginKind = "plugin";

    /// <summary>The key prefix of every plugin item; the rest of the key is <c>&lt;marketplace&gt;/&lt;plugin&gt;</c>.</summary>
    public const string PluginKeyPrefix = "plugin:";

    private const int MaxQueryWords = 32;
    private const int PrefixMinLength = 4;
    private const int CandidateRows = 200;
    private const double MinTrigramDice = 0.5;
    private const double ExactNameBonus = 50;
    private const double PrefixNameBonus = 25;

    // name, name_words, plugin, marketplace, description.
    private const string Bm25Weights = "1.0, 2.0, 1.0, 0.5, 1.0";

    private static readonly HashSet<string> StopWords = new(
        """
        a about above after again against all am an and any are as at be because been before being below
        between both but by can could did do does doing done down during each few for from further had has
        have having he her here hers him his how i if in into is it its itself just me more most my myself
        no nor not now of off on once only or other our ours out over own same she should so some such than
        that the their theirs them then there these they this those through to too under until up very was
        we were what when where which while who whom why will with would you your yours yourself yourselves
        """.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries),
        StringComparer.Ordinal
    );

    private readonly object _gate = new();
    private readonly SqliteConnection _connection;
    private readonly CapabilityItem[] _items; // rowid = index + 1, in every kind's table
    private readonly Dictionary<string, CapabilityItem> _byKey;
    private readonly Dictionary<string, KindIndex> _kinds; // by kind; a kind with no items has no entry
    private bool _disposed;

    private CapabilityCatalog(SqliteConnection connection, CapabilityItem[] items, Dictionary<string, KindIndex> kinds)
    {
        _connection = connection;
        _items = items;
        _kinds = kinds;
        _byKey = new Dictionary<string, CapabilityItem>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            _byKey[item.Key] = item;
        }
    }

    /// <summary>Every indexed item, in index order.</summary>
    public IReadOnlyList<CapabilityItem> Items => _items;

    /// <summary>Builds the index: one FTS5 table and vocabulary per kind. Item keys must be unique.</summary>
    /// <exception cref="ArgumentException">Two items share a key.</exception>
    public static CapabilityCatalog Build(IReadOnlyList<CapabilityItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var duplicate = items.GroupBy(i => i.Key, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw new ArgumentException($"Duplicate capability key '{duplicate.Key}'.", nameof(items));
        }

        var connection = new SqliteConnection("Data Source=:memory:;Pooling=False");
        try
        {
            connection.Open();
            var kinds = new Dictionary<string, KindIndex>(StringComparer.Ordinal);
            using (var transaction = connection.BeginTransaction())
            {
                // Table names are numbered, so kind text never reaches SQL.
                foreach (var group in items.Select((item, i) => (Item: item, RowId: i + 1L)).GroupBy(r => r.Item.Kind))
                {
                    var index = new KindIndex($"items{kinds.Count}", $"vocab{kinds.Count}", []);
                    kinds[group.Key] = index with { Vocabulary = IndexKind(connection, index, group) };
                }

                transaction.Commit();
            }

            return new CapabilityCatalog(connection, [.. items], kinds);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    /// <summary>Creates and fills one kind's item and vocabulary tables; returns its sorted vocabulary.</summary>
    private static string[] IndexKind(
        SqliteConnection connection,
        KindIndex index,
        IEnumerable<(CapabilityItem Item, long RowId)> rows
    )
    {
        Execute(
            connection,
            $"CREATE VIRTUAL TABLE {index.Items} USING fts5(name, name_words, plugin, marketplace, description, "
                + "tokenize = 'porter unicode61')"
        );
        // Persistent rank function, so ORDER BY rank (FTS5's fast path) uses the tuned weights.
        Execute(connection, $"INSERT INTO {index.Items}({index.Items}, rank) VALUES ('rank', 'bm25({Bm25Weights})')");
        Execute(connection, $"CREATE VIRTUAL TABLE {index.Vocab} USING fts5(word, tokenize = 'trigram')");

        var vocabulary = new SortedSet<string>(StringComparer.Ordinal);
        using var insert = connection.CreateCommand();
        insert.CommandText =
            $"INSERT INTO {index.Items}(rowid, name, name_words, plugin, marketplace, description) "
            + "VALUES ($id, $name, $words, $plugin, $mkt, $desc)";
        var id = insert.Parameters.Add("$id", SqliteType.Integer);
        var name = insert.Parameters.Add("$name", SqliteType.Text);
        var words = insert.Parameters.Add("$words", SqliteType.Text);
        var plugin = insert.Parameters.Add("$plugin", SqliteType.Text);
        var mkt = insert.Parameters.Add("$mkt", SqliteType.Text);
        var desc = insert.Parameters.Add("$desc", SqliteType.Text);
        foreach (var (item, rowId) in rows)
        {
            var nameWords = NameWords(item.Name);
            id.Value = rowId;
            name.Value = item.Name;
            words.Value = string.Join(' ', nameWords);
            plugin.Value = item.Plugin ?? string.Empty;
            mkt.Value = item.Marketplace ?? string.Empty;
            desc.Value = item.Description;
            _ = insert.ExecuteNonQuery();

            vocabulary.UnionWith(nameWords);
            vocabulary.UnionWith(Words(item.Name));
            vocabulary.UnionWith(Words(item.Plugin ?? string.Empty));
            vocabulary.UnionWith(Words(item.Marketplace ?? string.Empty));
            vocabulary.UnionWith(Words(item.Description));
        }

        using var insertWord = connection.CreateCommand();
        insertWord.CommandText = $"INSERT INTO {index.Vocab}(word) VALUES ($w)";
        var word = insertWord.Parameters.Add("$w", SqliteType.Text);
        foreach (var w in vocabulary)
        {
            word.Value = w;
            _ = insertWord.ExecuteNonQuery();
        }

        return [.. vocabulary];
    }

    /// <summary>
    /// One item per skill the gateway catalog lists (marketplace and workspace), keyed
    /// <c>skill:&lt;path&gt;</c>. Tiers are ignored: the catalog is what is admitted, not what is listed.
    /// </summary>
    public static IReadOnlyList<CapabilityItem> FromSkills(SandboxSkillCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var items = new List<CapabilityItem>();
        foreach (var marketplace in catalog.Marketplaces)
        {
            foreach (var plugin in marketplace.Plugins)
            {
                foreach (var skill in plugin.Skills)
                {
                    items.Add(
                        new CapabilityItem(
                            SkillKeyPrefix + skill.Path,
                            SkillKind,
                            skill.Name,
                            plugin.Name,
                            marketplace.Alias,
                            skill.Description
                        )
                    );
                }
            }
        }

        foreach (var skill in catalog.WorkspaceSkills)
        {
            items.Add(
                new CapabilityItem(SkillKeyPrefix + skill.Path, SkillKind, skill.Name, null, null, skill.Description)
            );
        }

        return items;
    }

    /// <summary>
    /// One item per plugin of each gateway marketplace, keyed <c>plugin:&lt;marketplace&gt;/&lt;plugin&gt;</c>
    /// and described by its <c>plugin.json</c>. A plugin the gateway leaves out (for example one with no
    /// skills) has no item.
    /// </summary>
    public static IReadOnlyList<CapabilityItem> FromPlugins(SandboxSkillCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return
        [
            .. catalog.Marketplaces.SelectMany(marketplace =>
                marketplace.Plugins.Select(plugin => new CapabilityItem(
                    PluginKeyPrefix + marketplace.Alias + "/" + plugin.Name,
                    PluginKind,
                    plugin.Name,
                    plugin.Name,
                    marketplace.Alias,
                    plugin.Description
                ))
            ),
        ];
    }

    /// <summary>
    /// One item per sub-agent template, keyed <c>agent:&lt;template key&gt;</c>, in ordinal key order (so
    /// the same templates always give the same items). The plugin is the part of the key before its first
    /// <c>:</c> (marketplace templates are keyed <c>plugin:name</c>); a template key carries no marketplace.
    /// </summary>
    public static IReadOnlyList<CapabilityItem> FromTemplates(IReadOnlyDictionary<string, SubAgentTemplate> templates)
    {
        ArgumentNullException.ThrowIfNull(templates);
        return
        [
            .. templates
                .OrderBy(t => t.Key, StringComparer.Ordinal)
                .Select(t =>
                {
                    var colon = t.Key.IndexOf(':', StringComparison.Ordinal);
                    return new CapabilityItem(
                        AgentKeyPrefix + t.Key,
                        AgentKind,
                        t.Key,
                        colon > 0 ? t.Key[..colon] : null,
                        null,
                        t.Value.Description ?? string.Empty
                    );
                }),
        ];
    }

    /// <summary>The skill path a skill key names.</summary>
    public static string SkillPathOf(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return key.StartsWith(SkillKeyPrefix, StringComparison.Ordinal)
            ? key[SkillKeyPrefix.Length..]
            : throw new ArgumentException($"'{key}' is not a skill key.", nameof(key));
    }

    /// <summary>The item with this key, if indexed.</summary>
    public bool TryGet(string key, out CapabilityItem item) => _byKey.TryGetValue(key, out item!);

    /// <summary>
    /// Ranked items of <paramref name="kind"/> for free text <paramref name="query"/>, best first, at most
    /// <paramref name="limit"/>. Any text is accepted: it is reduced to words and never reaches FTS5 as
    /// syntax. A query with no searchable word, or a kind with no items, returns an empty list.
    /// </summary>
    public IReadOnlyList<CapabilityItem> Search(string query, string kind, int limit) =>
        [.. SearchScored(query, kind, limit).Select(hit => hit.Item)];

    /// <summary>
    /// <see cref="Search"/> with each hit's score: the negated weighted BM25 of its kind's table plus the
    /// name bonus. Higher is better; scores compare only within one kind.
    /// </summary>
    internal IReadOnlyList<(CapabilityItem Item, double Score)> SearchScored(string query, string kind, int limit)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(kind);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        var words = QueryWords(query);
        if (words.Count == 0)
        {
            return [];
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_kinds.TryGetValue(kind, out var index))
            {
                return [];
            }

            var terms = words.Select(Term).ToList();
            var scores = RunMatch(index, terms);
            if (scores.Count < limit)
            {
                var corrections = words
                    .SelectMany(w => Corrections(index, w))
                    .Select(Term)
                    .Where(t => !terms.Contains(t))
                    .ToList();
                if (corrections.Count > 0)
                {
                    scores = RunMatch(index, [.. terms, .. corrections]);
                }
            }

            var normalized = query.Trim().ToLowerInvariant();
            var joined = string.Join('-', words);
            foreach (var rowId in scores.Keys.ToList())
            {
                scores[rowId] += NameBonus(_items[rowId - 1].Name, normalized, joined);
            }

            return
            [
                .. scores
                    .OrderByDescending(kv => kv.Value)
                    .ThenBy(kv => _items[kv.Key - 1].Name, StringComparer.Ordinal)
                    .Take(limit)
                    .Select(kv => (_items[kv.Key - 1], kv.Value)),
            ];
        }
    }

    /// <summary>
    /// Resolves a skill reference the way the gateway's <c>Skill</c> tool and the tiers config name one:
    /// an exact path, else an exact gateway name, else the trailing <c>:</c>-segments of one (a bare skill
    /// name, or <c>plugin:skill</c> where the gateway added the marketplace) that only one skill has. False
    /// when nothing or more than one skill matches.
    /// </summary>
    public bool TryResolveSkill(string nameOrPath, out CapabilityItem item)
    {
        var matches = MatchItem(SkillKind, nameOrPath);
        item = matches.Count == 1 ? matches[0] : null!;
        return matches.Count == 1;
    }

    /// <summary>
    /// Resolves config selectors to the keys of items of <paramref name="kind"/>, de-duplicated, in
    /// first-seen order. A selector is <c>&lt;kind&gt;:&lt;ref&gt;</c>, split at the FIRST colon:
    /// <list type="bullet">
    /// <item><c>skill:&lt;name|path&gt;</c> (see <see cref="TryResolveSkill"/>) and <c>agent:&lt;ref&gt;</c> (a
    /// template key, or the trailing <c>:</c>-segments of only one) name one item. A selector of the other
    /// of these two kinds is skipped: it belongs to that kind's call.</item>
    /// <item><c>plugin:&lt;marketplace&gt;/&lt;plugin&gt;</c> loads every item of <paramref name="kind"/> in that
    /// plugin. An item with no marketplace (an agent) matches on the plugin name alone. A plugin with nothing
    /// of this kind loads nothing and is not an issue; one the catalog has nothing from is Unknown.</item>
    /// </list>
    /// A selector that resolves to nothing goes to <paramref name="issues"/> and loads nothing. Any other
    /// kind (<c>tool:</c> included) is <see cref="SelectorProblem.Malformed"/>.
    /// </summary>
    /// <param name="selectors">The selectors, in config order.</param>
    /// <param name="issues">Receives one issue per selector that loads nothing.</param>
    /// <param name="kind">
    /// The kind to resolve for. <c>null</c> is the slice-1 form, kept until the host passes a kind: skills,
    /// with a selector of any other kind (<c>agent:</c> included) reported Malformed instead of skipped.
    /// </param>
    public IReadOnlyList<string> ResolveSelectors(
        IEnumerable<string> selectors,
        ICollection<SelectorIssue> issues,
        string? kind = null
    )
    {
        ArgumentNullException.ThrowIfNull(selectors);
        ArgumentNullException.ThrowIfNull(issues);
        var otherKindIsMalformed = kind is null;
        kind ??= SkillKind;
        var keys = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var selector in selectors)
        {
            var colon = selector?.IndexOf(':', StringComparison.Ordinal) ?? -1;
            if (selector is null || colon <= 0 || colon == selector.Length - 1)
            {
                issues.Add(new SelectorIssue(selector ?? string.Empty, SelectorProblem.Malformed, []));
                continue;
            }

            var selectorKind = selector[..colon];
            var reference = selector[(colon + 1)..];
            IReadOnlyList<CapabilityItem> resolved;
            switch (selectorKind)
            {
                case SkillKind or AgentKind when selectorKind != kind:
                    if (otherKindIsMalformed)
                    {
                        issues.Add(new SelectorIssue(selector, SelectorProblem.Malformed, []));
                    }

                    continue;
                case SkillKind or AgentKind:
                    var matches = MatchItem(kind, reference);
                    if (matches.Count != 1)
                    {
                        issues.Add(
                            new SelectorIssue(
                                selector,
                                matches.Count == 0 ? SelectorProblem.Unknown : SelectorProblem.Ambiguous,
                                [.. matches.Select(m => m.Name)]
                            )
                        );
                        continue;
                    }

                    resolved = matches;
                    break;
                case PluginKind:
                    var inPlugin = MatchPlugin(reference);
                    if (inPlugin.Count == 0)
                    {
                        issues.Add(new SelectorIssue(selector, SelectorProblem.Unknown, []));
                        continue;
                    }

                    resolved = [.. inPlugin.Where(i => i.Kind == kind)];
                    break;
                default:
                    issues.Add(new SelectorIssue(selector, SelectorProblem.Malformed, []));
                    continue;
            }

            foreach (var item in resolved)
            {
                if (seen.Add(item.Key))
                {
                    keys.Add(item.Key);
                }
            }
        }

        return keys;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _connection.Dispose();
        }
    }

    /// <summary>
    /// Items of <paramref name="kind"/> a reference names: the exact key <c>&lt;kind&gt;:&lt;reference&gt;</c>
    /// (a skill's path, an agent's template key), else every item with that exact name, else every item
    /// whose name ends with <c>:&lt;reference&gt;</c>, ordinal by name.
    /// </summary>
    private List<CapabilityItem> MatchItem(string kind, string reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        if (_byKey.TryGetValue(kind + ":" + reference, out var byKey) && byKey.Kind == kind)
        {
            return [byKey];
        }

        var ofKind = _items.Where(i => i.Kind == kind);
        var byName = ofKind.Where(i => string.Equals(i.Name, reference, StringComparison.Ordinal)).ToList();
        if (byName.Count > 0)
        {
            return byName;
        }

        // Trailing whole segments: `using-hitl` and `hitl:using-hitl` both name `gb-plugins:hitl:using-hitl`,
        // the form the gateway gives every skill of a plugin that two marketplaces carry.
        var suffix = ":" + reference;
        return
        [
            .. ofKind
                .Where(i => i.Name.EndsWith(suffix, StringComparison.Ordinal))
                .OrderBy(i => i.Name, StringComparer.Ordinal),
        ];
    }

    /// <summary>
    /// Items of every kind in plugin <c>&lt;marketplace&gt;/&lt;plugin&gt;</c>, in index order. An item with no
    /// marketplace matches on the plugin name alone.
    /// </summary>
    private List<CapabilityItem> MatchPlugin(string reference)
    {
        var slash = reference.IndexOf('/', StringComparison.Ordinal);
        if (slash <= 0 || slash == reference.Length - 1)
        {
            return [];
        }

        var marketplace = reference[..slash];
        var plugin = reference[(slash + 1)..];
        return
        [
            .. _items.Where(i =>
                string.Equals(i.Plugin, plugin, StringComparison.Ordinal)
                && (i.Marketplace is null || string.Equals(i.Marketplace, marketplace, StringComparison.Ordinal))
            ),
        ];
    }

    private Dictionary<long, double> RunMatch(KindIndex index, IReadOnlyList<string> terms)
    {
        using var command = _connection.CreateCommand();
        command.CommandText =
            $"SELECT rowid, rank FROM {index.Items} WHERE {index.Items} MATCH $match "
            + $"ORDER BY rank LIMIT {CandidateRows}";
        _ = command.Parameters.AddWithValue("$match", string.Join(" OR ", terms));
        var scores = new Dictionary<long, double>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            // rank (weighted bm25) is lower-is-better; negate so higher is better.
            scores[reader.GetInt64(0)] = -reader.GetDouble(1);
        }

        return scores;
    }

    /// <summary>
    /// The closest words of the kind's vocabulary for a query word none of them starts with (so the prefix
    /// match found nothing for it): all words with the best trigram Dice score, when that score is at least 0.5.
    /// </summary>
    private IEnumerable<string> Corrections(KindIndex index, string word)
    {
        if (word.Length < PrefixMinLength || HasVocabularyPrefix(index.Vocabulary, word))
        {
            return [];
        }

        var grams = Trigrams(word);
        using var command = _connection.CreateCommand();
        command.CommandText =
            $"SELECT word FROM {index.Vocab} WHERE {index.Vocab} MATCH $match ORDER BY rank LIMIT {CandidateRows}";
        _ = command.Parameters.AddWithValue("$match", string.Join(" OR ", grams.Select(Quote)));
        var scored = new List<(string Word, double Score)>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var candidate = reader.GetString(0);
                var score = Dice(grams, Trigrams(candidate));
                if (score >= MinTrigramDice)
                {
                    scored.Add((candidate, score));
                }
            }
        }

        if (scored.Count == 0)
        {
            return [];
        }

        var best = scored.Max(s => s.Score);
        return [.. scored.Where(s => s.Score == best).Select(s => s.Word)];
    }

    private static bool HasVocabularyPrefix(string[] vocabulary, string word)
    {
        var index = Array.BinarySearch(vocabulary, word, StringComparer.Ordinal);
        if (index >= 0)
        {
            return true;
        }

        index = ~index;
        return index < vocabulary.Length && vocabulary[index].StartsWith(word, StringComparison.Ordinal);
    }

    private static double NameBonus(string name, string normalizedQuery, string joinedWords)
    {
        var lowerName = name.ToLowerInvariant();
        var last = LastSegment(lowerName);
        if (normalizedQuery == lowerName || normalizedQuery == last || (joinedWords.Length > 0 && joinedWords == last))
        {
            return ExactNameBonus;
        }

        return
            normalizedQuery.Length >= 3
            && (
                lowerName.StartsWith(normalizedQuery, StringComparison.Ordinal)
                || last.StartsWith(normalizedQuery, StringComparison.Ordinal)
            )
            ? PrefixNameBonus
            : 0;
    }

    private static string LastSegment(string name)
    {
        var colon = name.LastIndexOf(':');
        return colon < 0 ? name : name[(colon + 1)..];
    }

    private static List<string> QueryWords(string query)
    {
        var words = new List<string>();
        foreach (var word in Words(query))
        {
            if (!StopWords.Contains(word) && !words.Contains(word))
            {
                words.Add(word);
                if (words.Count == MaxQueryWords)
                {
                    break;
                }
            }
        }

        return words;
    }

    private static string Term(string word) => word.Length >= PrefixMinLength ? Quote(word) + "*" : Quote(word);

    // Words hold only letters, marks and digits, so this is belt and braces: an FTS5 string literal
    // escapes '"' by doubling it.
    private static string Quote(string text) => "\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static IEnumerable<string> Words(string text) =>
        WordPattern().Matches(text).Select(m => m.Value.ToLowerInvariant());

    /// <summary>Name tokens with camelCase, snake_case, kebab-case and colons split apart, lower-cased.</summary>
    internal static List<string> NameWords(string name)
    {
        var words = new List<string>();
        foreach (Match part in WordPattern().Matches(name))
        {
            foreach (var piece in CamelBoundary().Split(part.Value))
            {
                if (piece.Length > 0)
                {
                    words.Add(piece.ToLowerInvariant());
                }
            }
        }

        return words;
    }

    private static HashSet<string> Trigrams(string word)
    {
        var grams = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i + 3 <= word.Length; i++)
        {
            _ = grams.Add(word.Substring(i, 3));
        }

        return grams;
    }

    private static double Dice(HashSet<string> a, HashSet<string> b) =>
        a.Count == 0 || b.Count == 0 ? 0 : 2.0 * a.Count(b.Contains) / (a.Count + b.Count);

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        _ = command.ExecuteNonQuery();
    }

    /// <summary>One kind's FTS5 item table, its trigram vocabulary table, and that vocabulary sorted ordinal.</summary>
    private sealed record KindIndex(string Items, string Vocab, string[] Vocabulary);

    [GeneratedRegex(@"[\p{L}\p{M}\p{N}]+", RegexOptions.CultureInvariant)]
    private static partial Regex WordPattern();

    [GeneratedRegex("(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])", RegexOptions.CultureInvariant)]
    private static partial Regex CamelBoundary();
}
