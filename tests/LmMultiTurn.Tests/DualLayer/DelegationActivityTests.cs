using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.DualLayer;
using FluentAssertions;
using Xunit;

namespace LmMultiTurn.Tests.DualLayer;

/// <summary>
/// The host's record of what the executor ran for one call. It is the planner's check on the
/// executor's prose, so it must speak up exactly when the run was not the call that was asked for.
/// </summary>
public class DelegationActivityTests
{
    private static readonly DelegatedToolCall Square22 = new(
        "calculate",
        """{"a":22,"b":22,"operation":"multiply"}""",
        "Second of three squares: compute exactly 22*22.",
        "p2"
    );

    private static ExecutedToolCall Ran(string args, bool isError = false) => new("calculate", args, isError);

    [Fact]
    public void Exactly_the_requested_call_once_adds_nothing()
    {
        // Key order differs: the match is on the JSON, not the text.
        DelegationActivity
            .Describe(Square22, [Ran("""{"operation":"multiply","a":22,"b":22}""")])
            .Should()
            .BeNull("the clean case must cost the planner no extra tokens");
    }

    [Fact]
    public void An_unrequested_next_step_is_listed_and_flagged()
    {
        // The case found by hand: asked for 22*22, the executor also ran 23*23 and reported on it.
        var note = DelegationActivity.Describe(
            Square22,
            [Ran("""{"a":22,"b":22,"operation":"multiply"}"""), Ran("""{"a":23,"b":23,"operation":"multiply"}""")]
        );

        note.Should()
            .Contain("""1. calculate {"a":22,"b":22,"operation":"multiply"}: succeeded (your call)""")
            .And.Contain(
                """2. calculate {"a":23,"b":23,"operation":"multiply"}: succeeded (extra: not the call you made)"""
            )
            .And.Contain("Make sure the report above answers your call");
    }

    [Fact]
    public void A_failed_requested_call_is_reported_without_blaming_extra_work()
    {
        var note = DelegationActivity.Describe(Square22, [Ran(Square22.ArgumentsJson, isError: true)]);

        note.Should().Contain("failed (your call)").And.NotContain("other than yours");
    }

    [Fact]
    public void Changed_arguments_are_named_on_the_planners_own_call_not_called_extra()
    {
        var note = DelegationActivity.Describe(
            Square22,
            [Ran("""{"a":2,"b":22,"operation":"multiply","model":"gpt-6-sol"}""")]
        );

        note.Should()
            .Contain("""(your call, sent with different arguments: "a" 22 -> 2; "model" added: "gpt-6-sol")""")
            .And.Contain("not what the tool did")
            .And.NotContain("extra")
            .And.NotContain("other than yours");
    }

    [Theory]
    [InlineData("""{"a":22,"b":22,"operation":"multiply","description":"","modelIntelligence":0}""")]
    [InlineData("""{"a":22,"b":22,"operation":"multiply","idempotency_key":null,"flag":false,"tags":[],"opts":{}}""")]
    public void Optional_arguments_left_empty_or_at_their_default_are_the_same_call(string ran)
    {
        // Found by hand: the executor filled the Agent tool's optional fields with "" and 0, and the
        // planner was told its call "was not run as given" and that an extra call ran.
        DelegationActivity.Describe(Square22, [Ran(ran)]).Should().BeNull();
    }

    [Fact]
    public void A_different_tool_is_extra_and_the_planners_call_was_not_run()
    {
        var note = DelegationActivity.Describe(Square22, [new ExecutedToolCall("read_file", "{}", false)]);

        note.Should()
            .Contain("(extra: not the call you made)")
            .And.Contain("Your call was not run.")
            .And.Contain("also ran calls other than yours");
    }

    [Fact]
    public void An_exact_match_later_in_the_run_is_the_planners_call_over_an_earlier_changed_one()
    {
        var note = DelegationActivity.Describe(
            Square22,
            [Ran("""{"a":2,"b":22,"operation":"multiply"}"""), Ran(Square22.ArgumentsJson)]
        );

        note.Should()
            .Contain("""1. calculate {"a":2,"b":22,"operation":"multiply"}: succeeded (extra: not the call you made)""")
            .And.Contain("""2. calculate {"a":22,"b":22,"operation":"multiply"}: succeeded (your call)""");
    }

    [Fact]
    public void No_tools_at_all_is_stated()
    {
        DelegationActivity.Describe(Square22, []).Should().Contain("No tools ran");
    }

    [Fact]
    public void Unobserved_arguments_fall_back_to_matching_the_name()
    {
        DelegationActivity.Describe(Square22, [new ExecutedToolCall("calculate", null, false)]).Should().BeNull();
    }

    [Fact]
    public void Long_arguments_are_cut_so_a_write_does_not_echo_its_content_back()
    {
        var write = new DelegatedToolCall("write_file", "{}", "Save the file.", "p1");
        var content = new string('x', 5000);

        var note = DelegationActivity.Describe(
            write,
            [new ExecutedToolCall("read_file", $$"""{"c":"{{content}}"}""", false)]
        );

        note!.Length.Should().BeLessThan(1000);
        note.Should().Contain("…");
    }

    [Fact]
    public void Observe_pairs_results_with_their_calls_and_skips_other_threads()
    {
        var activity = new DelegationActivity("executor-t1");

        activity.Observe(
            new ToolCallMessage
            {
                ToolCallId = "c1",
                FunctionName = "calculate",
                FunctionArgs = Square22.ArgumentsJson,
                ThreadId = "executor-t1",
            }
        );
        activity.Observe(
            new ToolCallResultMessage
            {
                ToolCallId = "c1",
                ToolName = "calculate",
                Result = "484",
                ThreadId = "executor-t1",
            }
        );
        // A sub-agent of the executor works on its own thread. Its calls are not the executor's.
        activity.Observe(
            new ToolCallResultMessage
            {
                ToolCallId = "c9",
                ToolName = "calculate",
                Result = "0",
                ThreadId = "subagent-x",
            }
        );

        activity.Executed.Should().Equal(new ExecutedToolCall("calculate", Square22.ArgumentsJson, false));
    }
}
