using System.Collections;

namespace TodoEval.Runner.Metrics;

/// <summary>
/// An immutable string-keyed tally with VALUE equality, serialized as a plain JSON object.
/// </summary>
/// <remarks>
/// The metrics records are C# <c>record</c>s, and a record whose member is a plain
/// <c>Dictionary</c> compares that member by REFERENCE — two rows carrying identical tallies would
/// test as different, which is exactly the trap a fixture-pinned test suite falls into silently.
/// Every tally the score object carries (per-tool error codes, run-level error codes, wait
/// outcomes) goes through this type so record equality means what it reads like.
/// </remarks>
internal sealed class CountMap : IReadOnlyDictionary<string, int>, IEquatable<CountMap>
{
    public static readonly CountMap Empty = new(new Dictionary<string, int>(StringComparer.Ordinal));

    private readonly Dictionary<string, int> _counts;

    private CountMap(Dictionary<string, int> counts) => _counts = counts;

    public static CountMap From(IEnumerable<KeyValuePair<string, int>> counts)
    {
        var merged = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (key, count) in counts)
        {
            merged[key] = merged.TryGetValue(key, out var seen) ? seen + count : count;
        }

        return merged.Count == 0 ? Empty : new CountMap(merged);
    }

    /// <summary>Sums any number of tallies key by key.</summary>
    public static CountMap Merge(IEnumerable<CountMap> sources) => From(sources.SelectMany(source => source));

    public CountMap Add(string key, int count = 1) => From(_counts.Append(new KeyValuePair<string, int>(key, count)));

    public int this[string key] => _counts[key];
    public IEnumerable<string> Keys => _counts.Keys;
    public IEnumerable<int> Values => _counts.Values;
    public int Count => _counts.Count;

    public bool ContainsKey(string key) => _counts.ContainsKey(key);

    public bool TryGetValue(string key, out int value) => _counts.TryGetValue(key, out value);

    public IEnumerator<KeyValuePair<string, int>> GetEnumerator() => _counts.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public bool Equals(CountMap? other) =>
        other is not null
        && (
            ReferenceEquals(this, other)
            || (
                _counts.Count == other._counts.Count
                && _counts.All(kvp => other.TryGetValue(kvp.Key, out var v) && v == kvp.Value)
            )
        );

    public override bool Equals(object? obj) => Equals(obj as CountMap);

    public override int GetHashCode()
    {
        // Order-independent so two equal maps built in different key orders hash alike.
        var hash = _counts.Count;
        foreach (var (key, count) in _counts)
        {
            hash ^= HashCode.Combine(key, count);
        }

        return hash;
    }
}
