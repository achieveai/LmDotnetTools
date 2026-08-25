namespace AchieveAi.LmDotnetTools.LmEval.Tests.Infrastructure;

/// <summary>
/// The shared vocabulary for the harness tests: a one-criterion 0-10 rubric with a pass threshold
/// of 6, and a candidate builder. Kept deliberately small so each test's setup is only the part it
/// actually varies.
/// </summary>
internal static class HarnessFixtures
{
    public const string TaskType = "code-review";

    /// <summary>Scores at or above this are a pass; below it, a fail. The straddle boundary.</summary>
    public const int PassThreshold = 6;

    public static Rubric Rubric(params RubricCriterion[] criteria) =>
        new()
        {
            RubricId = "test-rubric",
            RubricVersion = "1.0",
            TaskType = TaskType,
            MinScore = 0,
            MaxScore = 10,
            PassThreshold = PassThreshold,
            Criteria = criteria.Length > 0 ? criteria : [Criterion("quality")],
        };

    public static RubricCriterion Criterion(string id, double weight = 1.0) =>
        new()
        {
            CriterionId = id,
            Description = $"How well the review satisfies {id}.",
            Anchors = new Dictionary<int, string>
            {
                [0] = "no finding cites a file and line that resolves",
                [5] = "some findings cite a file and line that resolves",
                [10] = "every finding cites a file and line that resolves",
            },
            Weight = weight,
        };

    public static Candidate Candidate(string? generatorFamily = null, string content = "a review") =>
        new()
        {
            CandidateId = "cand-1",
            TaskType = TaskType,
            TaskInput = "Grade this code review:",
            Content = content,
            GeneratorFamily = generatorFamily,
        };
}
