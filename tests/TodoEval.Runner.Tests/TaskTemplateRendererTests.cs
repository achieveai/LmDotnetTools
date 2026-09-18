namespace TodoEval.Runner.Tests;

public class TaskTemplateRendererTests
{
    [Fact]
    public void Render_SubstitutesEveryOccurrence()
    {
        var rendered = TaskTemplateRenderer.Render(
            "# Task\nBuild a plan for {TOPIC}. When done, review the {TOPIC} plan.",
            "a team offsite"
        );

        rendered.Should().Be("# Task\nBuild a plan for a team offsite. When done, review the a team offsite plan.");
    }

    [Fact]
    public void Render_IsCaseSensitive_LowercasePlaceholderIsNotSubstituted()
    {
        // {topic} is not the contract token; a template author typo must surface as the missing-
        // placeholder error rather than silently shipping "{topic}" text to the model.
        var act = () => TaskTemplateRenderer.Render("Do {topic}.", "x");

        act.Should().Throw<InvalidOperationException>().WithMessage("*{TOPIC}*");
    }

    [Fact]
    public void Render_TemplateWithoutPlaceholder_Throws()
    {
        var act = () => TaskTemplateRenderer.Render("A task that ignores its topic.", "x");

        act.Should().Throw<InvalidOperationException>().WithMessage("*placeholder*");
    }

    [Fact]
    public void Render_BlankTopic_Throws()
    {
        var act = () => TaskTemplateRenderer.Render("Do {TOPIC}.", "  ");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Render_SubstitutesTheSeedWord()
    {
        var rendered = TaskTemplateRenderer.Render("Project code name: {SEED}.\nBuild {SEED}.", "a topic", "aurora");

        rendered.Should().Be("Project code name: aurora.\nBuild aurora.");
    }

    [Fact]
    public void Render_SeedOnlyTemplate_NeedsNoTopicPlaceholder()
    {
        // Every compaction-eval task varies by seed word alone: the work is identical on purpose, so
        // demanding a {TOPIC} would reject the entire suite.
        var act = () => TaskTemplateRenderer.Render("Do the work. Code name {SEED}.", "a topic", "basalt");

        act.Should().NotThrow();
    }

    [Fact]
    public void Render_NoSeedSupplied_LeavesTheSeedTokenAlone()
    {
        // A task that declares no seeds in meta.json has no word to substitute. Leaving the token is
        // visible in the transcript; inventing one would silently fake the axis.
        TaskTemplateRenderer.Render("Do {TOPIC} as {SEED}.", "a topic").Should().Be("Do a topic as {SEED}.");
    }

    [Fact]
    public void ExtractTaskMessage_TakesOnlyTheTextBelowTheMarker()
    {
        var file =
            "# todo-eval scripted task\n\nHeader docs about the eval.\n\n---\n\nDo the {TOPIC} release.\nSecond line.\n";

        EvalAssets.ExtractTaskMessage(file, "task.md").Message.Should().Be("Do the {TOPIC} release.\nSecond line.");
    }

    [Fact]
    public void ExtractTaskMessage_MarkerLineMustBeExactlyDashes()
    {
        // A "---" inside the message body (e.g. a markdown rule further down) splits at the FIRST
        // marker only; a header without any marker means the whole file is the message.
        var withoutMarker = "Do the {TOPIC} release.";
        EvalAssets.ExtractTaskMessage(withoutMarker, "task.md").Message.Should().Be("Do the {TOPIC} release.");

        var twoMarkers = "docs\n---\nmessage top\n---\nmessage bottom";
        EvalAssets.ExtractTaskMessage(twoMarkers, "task.md").Message.Should().Be("message top\n---\nmessage bottom");
    }

    [Fact]
    public void ExtractTaskMessage_SteerHeadingEndsTheFirstMessageAndBeginsTheCorrection()
    {
        var file = "docs\n---\nWrite the report on {SEED}.\n\n## steer\n\nCorrection: 15 rows, not 10.\n";

        var (message, steer) = EvalAssets.ExtractTaskMessage(file, "task.md");

        message.Should().Be("Write the report on {SEED}.");
        steer.Should().Be("Correction: 15 rows, not 10.");
    }

    [Fact]
    public void ExtractTaskMessage_NoSteerHeading_YieldsNoCorrection()
    {
        EvalAssets.ExtractTaskMessage("docs\n---\nDo {TOPIC}.\n", "task.md").Steer.Should().BeNull();
    }

    [Fact]
    public void ExtractTaskMessage_SteerHeadingWithNothingBelow_Throws()
    {
        // An empty correction would be sent as an empty second message, which the host rejects far
        // from here — a task file that promises a steer and carries none is a corpus error.
        var act = () => EvalAssets.ExtractTaskMessage("docs\n---\nDo {TOPIC}.\n\n## steer\n\n", "task.md");

        act.Should().Throw<InvalidOperationException>().WithMessage("*steer*");
    }

    [Fact]
    public void ExtractTaskMessage_MarkerWithNothingBelow_Throws()
    {
        var act = () => EvalAssets.ExtractTaskMessage("docs\n---\n   \n", "task.md");

        act.Should().Throw<InvalidOperationException>().WithMessage("*nothing below*");
    }

    [Fact]
    public void ExtractTaskMessage_HandlesCrLf()
    {
        EvalAssets.ExtractTaskMessage("docs\r\n---\r\nDo {TOPIC}.\r\n", "task.md").Message.Should().Be("Do {TOPIC}.");
    }
}
