namespace AchieveAi.LmDotnetTools.LmEval.Tests.Infrastructure;

/// <summary>
/// A hand-written <see cref="IJudge"/> double that COUNTS its invocations. Counting is the point:
/// several of this slice's claims ("no model call", "escalates exactly once", "never reaches the
/// arbiter") are claims about how many times a judge ran, and only a counter can carry them.
/// </summary>
internal sealed class FakeJudge : IJudge
{
    private readonly double _score;
    private readonly Exception? _fault;

    public FakeJudge(
        string judgeId,
        string modelFamily,
        double score = 8.0,
        double confidence = 0.9,
        bool abstained = false,
        Exception? fault = null
    )
    {
        JudgeId = judgeId;
        ModelId = $"{modelFamily}/model";
        ModelFamily = modelFamily;
        _score = score;
        Confidence = confidence;
        Abstained = abstained;
        _fault = fault;
    }

    public string JudgeId { get; }

    public string ModelId { get; }

    public string ModelFamily { get; }

    public double Confidence { get; }

    public bool Abstained { get; }

    /// <summary>How many times <see cref="JudgeAsync"/> was entered.</summary>
    public int Calls { get; private set; }

    /// <summary>The reference the harness passed on the most recent call, if any.</summary>
    public string? LastReference { get; private set; }

    /// <summary>Every candidate this judge was asked about, in call order.</summary>
    public List<Candidate> SeenCandidates { get; } = [];

    public Task<Ballot> JudgeAsync(
        Candidate candidate,
        Rubric rubric,
        JudgeContext context,
        CancellationToken cancellationToken
    )
    {
        Calls++;
        LastReference = context.Reference;
        SeenCandidates.Add(candidate);

        if (_fault is not null)
        {
            return Task.FromException<Ballot>(_fault);
        }

        return Task.FromResult(
            new Ballot
            {
                JudgeId = JudgeId,
                ModelId = ModelId,
                ModelFamily = ModelFamily,
                CriterionScores = rubric.Criteria.ToDictionary(c => c.CriterionId, _ => (int)Math.Round(_score)),
                WeightedScore = _score,
                Reasoning = $"{JudgeId} scored {_score}.",
                Confidence = Confidence,
                Abstained = Abstained,
                AbstainReason = Abstained ? "declined" : null,
            }
        );
    }
}
