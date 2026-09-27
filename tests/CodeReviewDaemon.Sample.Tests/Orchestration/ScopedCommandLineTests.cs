using CodeReviewDaemon.Sample.Orchestration;

namespace CodeReviewDaemon.Sample.Tests.Orchestration;

/// <summary>
/// <see cref="ScopedCommandLine.Parse"/> is the pure, DI-free gate <c>Program.cs</c> calls before it builds
/// any configuration, host, or provider (security review round 1). These tests pin every well-formed
/// shape and — the point of the whole exercise — that a fat-fingered scoped prefix comes back
/// <see cref="ScopedCommandKind.Malformed"/> rather than silently falling through to
/// <c>ReviewProfileArgs.Extract</c>/normal daemon startup.
/// </summary>
public sealed class ScopedCommandLineTests
{
    [Fact]
    public void Recognizes_a_well_formed_workflow_operation()
    {
        var result = ScopedCommandLine.Parse(["--workflow-operation", "list-runs"]);

        result.Kind.Should().Be(ScopedCommandKind.WorkflowOperation);
        result.WorkflowOperation.Should().Be("list-runs");
    }

    [Fact]
    public void Recognizes_a_well_formed_list_candidate_prs()
    {
        var result = ScopedCommandLine.Parse(["--list-candidate-prs"]);

        result.Kind.Should().Be(ScopedCommandKind.ListCandidatePrs);
    }

    [Fact]
    public void Recognizes_a_well_formed_run_pr()
    {
        var result = ScopedCommandLine.Parse(["--run-pr", "acme/widgets", "7", "headsha", "basesha"]);

        result.Kind.Should().Be(ScopedCommandKind.RunPr);
        result.RunPrRepoKey.Should().Be("acme/widgets");
        result.RunPrId.Should().Be("7");
        result.RunPrHeadSha.Should().Be("headsha");
        result.RunPrBaseSha.Should().Be("basesha");
    }

    /// <summary>
    /// Task #82's redo goes through the SAME parser as task #81's commands rather than its own
    /// <c>args is [...]</c> match in Program.cs, so it gets the malformed-arity guard below for free.
    /// </summary>
    [Fact]
    public void Recognizes_a_well_formed_redo_artifact_branch()
    {
        var result = ScopedCommandLine.Parse(["--redo-artifact-branch", "acme/widgets", "7"]);

        result.Kind.Should().Be(ScopedCommandKind.RedoArtifactBranch);
        result.RedoRepoKey.Should().Be("acme/widgets");
        result.RedoPrId.Should().Be("7");
    }

    [Fact]
    public void Is_None_for_ordinary_daemon_startup_args()
    {
        ScopedCommandLine.Parse([]).Kind.Should().Be(ScopedCommandKind.None);
        ScopedCommandLine.Parse(["--urls", "http://localhost:5000"]).Kind.Should().Be(ScopedCommandKind.None);
    }

    [Theory]
    // InlineData cannot carry a nested string[] (CS0182), so each case is one space-joined string split below.
    [InlineData("--workflow-operation")]
    [InlineData("--workflow-operation a b")]
    [InlineData("--list-candidate-prs extra")]
    [InlineData("--run-pr")]
    [InlineData("--run-pr acme/widgets")]
    [InlineData("--run-pr acme/widgets 7 headsha")]
    [InlineData("--run-pr acme/widgets 7 headsha basesha extra")]
    [InlineData("--redo-artifact-branch")]
    [InlineData("--redo-artifact-branch acme/widgets")]
    [InlineData("--redo-artifact-branch acme/widgets 7 extra")]
    public void Is_Malformed_rather_than_None_for_a_known_prefix_with_wrong_arity(string spaceJoinedArgs)
    {
        // The critical distinction: an unrecognized flag falls through to None (ordinary daemon startup),
        // but a KNOWN scoped prefix with the wrong number of arguments must never fall through — it must
        // fail usage instead, carrying a message for Program.cs to print to stderr.
        var args = spaceJoinedArgs.Split(' ');

        var result = ScopedCommandLine.Parse(args);

        result.Kind.Should().Be(ScopedCommandKind.Malformed);
        result.Error.Should().NotBeNullOrWhiteSpace();
        result.Error.Should().Contain(args[0]);
    }
}
