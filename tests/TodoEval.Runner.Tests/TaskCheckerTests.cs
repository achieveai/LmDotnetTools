using TodoEval.Runner.Sweep;

namespace TodoEval.Runner.Tests;

/// <summary>
/// The J1 layer's one rule: a checker may report a verdict, and may report that it could not judge,
/// but must never be able to FAIL a run by failing itself. Every mapping below is that rule.
/// </summary>
public class TaskCheckerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"todo-eval-j1-{Guid.NewGuid():N}");

    public TaskCheckerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    private string WriteScore(string json)
    {
        var path = Path.Combine(_dir, "score.json");
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void CleanExitWithAPassingScore_IsAPass()
    {
        var path = WriteScore(
            """
            { "task": "c1", "outcome": "pass", "score": 1.0,
              "checks": [ { "name": "test_one", "pass": true }, { "name": "test_two", "pass": true } ] }
            """
        );

        var result = J1Result.From(exitCode: 0, path);

        result.Outcome.Should().Be("pass");
        result.Score.Should().Be(1.0);
        result.FailedChecks.Should().BeEmpty();
        result.Judged.Should().BeTrue();
    }

    [Fact]
    public void APartialScore_NamesTheChecksThatDidNotPass()
    {
        var path = WriteScore(
            """
            { "task": "c1", "outcome": "partial", "score": 0.5,
              "checks": [ { "name": "test_one", "pass": true },
                          { "name": "test_two", "pass": false, "detail": "AssertionError" } ] }
            """
        );

        var result = J1Result.From(exitCode: 0, path);

        result.Outcome.Should().Be("partial");
        result.Score.Should().Be(0.5);
        result.FailedChecks.Should().Equal("test_two");
    }

    [Fact]
    public void ExitTwo_IsUnjudgedAndSaysSo()
    {
        // The contract's "I could not judge this". Never a fail: the run may have been perfect.
        var path = WriteScore("""{ "outcome": "pass", "score": 1.0 }""");

        var result = J1Result.From(J1Result.CouldNotJudgeExitCode, path);

        result.Outcome.Should().Be(J1Result.Unjudged);
        result.Score.Should().BeNull();
        result.Judged.Should().BeFalse();
        result.Error.Should().Contain("could not judge");
    }

    [Fact]
    public void AnyOtherNonZeroExit_IsAlsoUnjudged()
    {
        var result = J1Result.From(exitCode: 1, Path.Combine(_dir, "score.json"), "NullReferenceException");

        result.Outcome.Should().Be(J1Result.Unjudged);
        result.Error.Should().Contain("exited 1").And.Contain("NullReferenceException");
    }

    [Fact]
    public void CleanExitButNoScoreFile_IsUnjudged()
    {
        var result = J1Result.From(exitCode: 0, Path.Combine(_dir, "absent.json"));

        result.Outcome.Should().Be(J1Result.Unjudged);
        result.Error.Should().Contain("no score file");
    }

    [Fact]
    public void AnUnparseableScoreFile_IsUnjudged()
    {
        var result = J1Result.From(exitCode: 0, WriteScore("this is not json {{{"));

        result.Outcome.Should().Be(J1Result.Unjudged);
        result.Error.Should().Contain("not a readable score file");
    }

    [Fact]
    public void AScoreFileWithoutAnOutcome_IsUnjudged()
    {
        // A score with no verdict cannot be turned into one; a 0.0 here would read as total failure.
        var result = J1Result.From(exitCode: 0, WriteScore("""{ "task": "c1", "score": 0.75 }"""));

        result.Outcome.Should().Be(J1Result.Unjudged);
        result.Score.Should().BeNull();
    }

    [Fact]
    public void AnOutcomeWithoutAScore_KeepsTheOutcomeAndReportsNoNumber()
    {
        var result = J1Result.From(exitCode: 0, WriteScore("""{ "outcome": "fail", "checks": [] }"""));

        result.Outcome.Should().Be("fail");
        result.Score.Should().BeNull("an absent number is not a zero");
    }

    [Fact]
    public async Task ATaskWithNoCheckerHasNoJ1AtAll()
    {
        // Distinct from unjudged: there is nothing to judge with, so the run claims no J1 either way.
        var task = new EvalTaskAsset
        {
            Id = "no-checker",
            Dir = _dir,
            Template = "Do {TOPIC}.",
            ExpectedBoard = null,
        };

        var result = await new PwshTaskChecker(TextWriter.Null).JudgeAsync(task, _dir, _dir, CancellationToken.None);

        result.Should().BeNull();
    }
}
