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
    public void ExtractTaskMessage_TakesOnlyTheTextBelowTheMarker()
    {
        var file =
            "# todo-eval scripted task\n\nHeader docs about the eval.\n\n---\n\nDo the {TOPIC} release.\nSecond line.\n";

        EvalAssets.ExtractTaskMessage(file, "task.md").Should().Be("Do the {TOPIC} release.\nSecond line.");
    }

    [Fact]
    public void ExtractTaskMessage_MarkerLineMustBeExactlyDashes()
    {
        // A "---" inside the message body (e.g. a markdown rule further down) splits at the FIRST
        // marker only; a header without any marker means the whole file is the message.
        var withoutMarker = "Do the {TOPIC} release.";
        EvalAssets.ExtractTaskMessage(withoutMarker, "task.md").Should().Be("Do the {TOPIC} release.");

        var twoMarkers = "docs\n---\nmessage top\n---\nmessage bottom";
        EvalAssets.ExtractTaskMessage(twoMarkers, "task.md").Should().Be("message top\n---\nmessage bottom");
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
        EvalAssets.ExtractTaskMessage("docs\r\n---\r\nDo {TOPIC}.\r\n", "task.md").Should().Be("Do {TOPIC}.");
    }
}
