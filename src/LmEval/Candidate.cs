namespace AchieveAi.LmDotnetTools.LmEval;

/// <summary>
/// One thing to be judged. <see cref="TaskType"/> is the baseline partition key: scores are
/// compared only within a task type, never across — a 6/10 code review and a 6/10 summarization
/// are not comparable quantities.
/// </summary>
public sealed record Candidate
{
    /// <summary>Stable identity of this candidate within its corpus or run.</summary>
    public required string CandidateId { get; init; }

    /// <summary>The baseline partition key. Never compare scores across task types.</summary>
    public required string TaskType { get; init; }

    /// <summary>The task as posed — the prompt/diff/question the candidate answers.</summary>
    public required string TaskInput { get; init; }

    /// <summary>The candidate output being judged.</summary>
    public required string Content { get; init; }

    /// <summary>
    /// Optional independently-produced reference answer. The single largest accuracy lever
    /// available to a judge.
    /// </summary>
    public string? Reference { get; init; }

    /// <summary>Which arm produced this candidate. Null for a corpus item with no variant.</summary>
    public string? VariantId { get; init; }

    /// <summary>The model that produced <see cref="Content"/>. Never rendered into a judge prompt.</summary>
    public string? ModelId { get; init; }

    /// <summary>
    /// Model family of whatever produced <see cref="Content"/>. Required for generator-family
    /// exclusion; when null, exclusion cannot be applied and the verdict records that fact.
    /// Resolved by the host — LmEval does not own a model taxonomy.
    /// </summary>
    public string? GeneratorFamily { get; init; }

    /// <summary>Host-supplied side data. Never rendered into a judge prompt by the harness.</summary>
    public IReadOnlyDictionary<string, string> Metadata { get; init; } =
        new Dictionary<string, string>();
}
