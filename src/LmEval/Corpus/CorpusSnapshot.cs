using System.Security.Cryptography;
using System.Text;

namespace AchieveAi.LmDotnetTools.LmEval.Corpus;

/// <summary>
/// A frozen, countable set of recorded candidates, together with the hash that identifies it.
/// <para>
/// Countable, not streamed, on purpose. <see cref="Items"/>'s count is the denominator every rate
/// in an eval run is reported over, and an item the run failed to reach still occupies it. A
/// streaming corpus could not supply that denominator without having already been enumerated, so
/// the run would end up dividing by what it managed to process — which is precisely the
/// flattering-by-declining-to-score failure the denominator rule exists to close.
/// </para>
/// </summary>
public sealed record CorpusSnapshot
{
    /// <summary>
    /// Field separator inside the hashed text. A unit separator rather than a printable character,
    /// so that no candidate's own content can forge a field boundary and hash as a different
    /// corpus.
    /// </summary>
    private const char Separator = '\u001f';

    private CorpusSnapshot(
        string corpusId,
        IReadOnlyList<Candidate> items,
        string snapshotHash,
        string taskType
    )
    {
        CorpusId = corpusId;
        Items = items;
        SnapshotHash = snapshotHash;
        TaskType = taskType;
    }

    /// <summary>Stable identity of the corpus this snapshot was taken from.</summary>
    public string CorpusId { get; }

    /// <summary>The single task type every item in the snapshot carries.</summary>
    public string TaskType { get; }

    /// <summary>The recorded candidates, in a fixed order.</summary>
    public IReadOnlyList<Candidate> Items { get; }

    /// <summary>
    /// Content hash over every item's identity and scored content. <b>Computed here, never
    /// supplied</b> — a caller-supplied hash could name a snapshot it does not describe, and the
    /// comparison refusal built on it would then pass a genuinely incomparable pair.
    /// </summary>
    public string SnapshotHash { get; }

    /// <summary>How many items the run's rates are reported over.</summary>
    public int Size => Items.Count;

    /// <summary>
    /// Freezes a set of recorded candidates into a snapshot.
    /// </summary>
    /// <param name="corpusId">Stable identity of the corpus.</param>
    /// <param name="items">
    /// The candidates. Must be non-empty, must all carry the same task type — scores are never
    /// comparable across task types — and must carry distinct candidate ids, since a duplicated id
    /// would double-count one item into the denominator and make its verdict unattributable.
    /// </param>
    /// <exception cref="ArgumentException">
    /// The set is empty, mixes task types, or repeats a candidate id.
    /// </exception>
    public static CorpusSnapshot Create(string corpusId, IReadOnlyList<Candidate> items)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(corpusId);
        ArgumentNullException.ThrowIfNull(items);

        if (items.Count == 0)
        {
            throw new ArgumentException(
                "A corpus snapshot needs at least one item: every rate an eval run reports is over "
                    + "the snapshot's item count, and an empty denominator makes each of them "
                    + "undefined rather than zero.",
                nameof(items)
            );
        }

        var taskType = items[0].TaskType;
        var mixed = items.FirstOrDefault(i =>
            !string.Equals(i.TaskType, taskType, StringComparison.Ordinal)
        );
        if (mixed is not null)
        {
            throw new ArgumentException(
                $"Corpus '{corpusId}' mixes task types '{taskType}' and '{mixed.TaskType}'. A "
                    + "score is meaningful only relative to other scores of the same task type, so "
                    + "a mean over both is not a worse number — it is a different quantity.",
                nameof(items)
            );
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var duplicate = items.FirstOrDefault(i => !seen.Add(i.CandidateId));
        if (duplicate is not null)
        {
            throw new ArgumentException(
                $"Corpus '{corpusId}' repeats candidate id '{duplicate.CandidateId}'. A repeated id "
                    + "counts one item twice into the denominator and leaves its verdict "
                    + "unattributable.",
                nameof(items)
            );
        }

        return new CorpusSnapshot(corpusId, items, ComputeHash(corpusId, items), taskType);
    }

    /// <summary>
    /// Hashes exactly what a candidate is scored on. The variant and the generator's identity are
    /// in it because a corpus that swapped the B arm for a different model's output is a different
    /// corpus, even at identical inputs; <c>Metadata</c> is not, because the harness never renders
    /// it into a judge prompt and so it cannot move a score.
    /// </summary>
    private static string ComputeHash(string corpusId, IReadOnlyList<Candidate> items)
    {
        var builder = new StringBuilder();
        _ = builder.Append(corpusId).Append('\n');

        // Ordered by id rather than by position: reading the same corpus back in a different row
        // order is the same corpus, and a hash that said otherwise would refuse a comparison that
        // is entirely legitimate.
        foreach (var item in items.OrderBy(i => i.CandidateId, StringComparer.Ordinal))
        {
            _ = builder
                .Append(item.CandidateId)
                .Append(Separator)
                .Append(item.TaskType)
                .Append(Separator)
                .Append(item.VariantId ?? string.Empty)
                .Append(Separator)
                .Append(item.ModelId ?? string.Empty)
                .Append(Separator)
                .Append(item.GeneratorFamily ?? string.Empty)
                .Append(Separator)
                .Append(item.TaskInput)
                .Append(Separator)
                .Append(item.Content)
                .Append(Separator)
                .Append(item.Reference ?? string.Empty)
                .Append('\n');
        }

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexString(digest).ToLowerInvariant();
    }
}

/// <summary>
/// Loads a frozen corpus of recorded candidates. Implemented by the <b>host</b>, not by
/// <c>LmEval</c>: the recorded pairs live in the host's database under the host's schema, and this
/// library owns no persistence.
/// </summary>
public interface ICorpusReader
{
    /// <summary>Loads one named corpus.</summary>
    /// <param name="corpusId">Which corpus to load.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    Task<CorpusSnapshot> LoadAsync(string corpusId, CancellationToken cancellationToken);
}
