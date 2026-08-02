using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmMultiTurn.ClientTools;
using FluentAssertions;
using Xunit;

namespace LmMultiTurn.Tests.ClientTools;

/// <summary>
/// Provider-level tests for <see cref="AskUserQuestionToolProvider"/> (issue #246), patterned on
/// <c>WaitToolProviderTests</c>: invokes the <see cref="FunctionDescriptor"/> handler directly so
/// the JSON args parsing, validation error codes, and the <c>Deferred</c> result on a valid call are
/// all exercised without needing a full <c>MultiTurnAgentLoop</c>.
/// </summary>
public class AskUserQuestionToolProviderTests
{
    private readonly FunctionDescriptor _tool;

    public AskUserQuestionToolProviderTests()
    {
        var provider = new AskUserQuestionToolProvider();
        _tool = provider.GetFunctions().Single(f => f.Contract.Name == AskUserQuestionToolProvider.ToolName);
    }

    private static string ValidArgs() => JsonSerializer.Serialize(new
    {
        context = "Need to know which color to use.",
        questions = new[]
        {
            new
            {
                prompt = "Which color?",
                options = new object[]
                {
                    new { label = "Red" },
                    new { label = "Blue", value = "blue-value" },
                },
            },
        },
    });

    private Task<ToolHandlerResult> InvokeAsync(string argsJson, string? toolCallId = "tc_1") =>
        _tool.Handler(argsJson, new ToolCallContext { ToolCallId = toolCallId }, CancellationToken.None);

    private static string ErrorCode(ToolHandlerResult result)
    {
        result.Should().BeOfType<ToolHandlerResult.Resolved>();
        var resolved = (ToolHandlerResult.Resolved)result;
        resolved.Payload.IsError.Should().BeTrue();
        return resolved.Payload.ErrorCode!;
    }

    [Fact]
    public async Task ValidCall_ReturnsDeferred()
    {
        var result = await InvokeAsync(ValidArgs());
        result.Should().BeOfType<ToolHandlerResult.Deferred>();
    }

    [Fact]
    public async Task ValidCall_WithFourQuestions_ReturnsDeferred()
    {
        var args = JsonSerializer.Serialize(new
        {
            context = "batch",
            questions = Enumerable.Range(0, 4).Select(i => new
            {
                prompt = $"Q{i}",
                options = new[] { new { label = "A" }, new { label = "B" } },
            }),
        });

        var result = await InvokeAsync(args);
        result.Should().BeOfType<ToolHandlerResult.Deferred>();
    }

    [Fact]
    public async Task MissingToolCallId_ReturnsError()
    {
        var result = await InvokeAsync(ValidArgs(), toolCallId: null);
        ErrorCode(result).Should().Be("missing_tool_call_id");
    }

    [Fact]
    public async Task MissingToolCallId_Empty_ReturnsError()
    {
        var result = await InvokeAsync(ValidArgs(), toolCallId: "");
        ErrorCode(result).Should().Be("missing_tool_call_id");
    }

    [Fact]
    public async Task MalformedJson_ReturnsInvalidArgs()
    {
        var result = await InvokeAsync("{not json");
        ErrorCode(result).Should().Be("invalid_args");
    }

    [Fact]
    public async Task EmptyArgs_ReturnsInvalidArgs()
    {
        var result = await InvokeAsync("");
        ErrorCode(result).Should().Be("invalid_args");
    }

    [Fact]
    public async Task MissingContext_ReturnsError()
    {
        var args = JsonSerializer.Serialize(new
        {
            questions = new[] { new { prompt = "Q", options = new[] { new { label = "A" } } } },
        });

        var result = await InvokeAsync(args);
        ErrorCode(result).Should().Be("missing_context");
    }

    [Fact]
    public async Task EmptyContext_ReturnsError()
    {
        var args = JsonSerializer.Serialize(new
        {
            context = "   ",
            questions = new[] { new { prompt = "Q", options = new[] { new { label = "A" } } } },
        });

        var result = await InvokeAsync(args);
        ErrorCode(result).Should().Be("missing_context");
    }

    [Fact]
    public async Task ZeroQuestions_ReturnsInvalidQuestionCount()
    {
        var args = JsonSerializer.Serialize(new { context = "ctx", questions = Array.Empty<object>() });
        var result = await InvokeAsync(args);
        ErrorCode(result).Should().Be("invalid_question_count");
    }

    [Fact]
    public async Task FiveQuestions_ReturnsInvalidQuestionCount()
    {
        var args = JsonSerializer.Serialize(new
        {
            context = "ctx",
            questions = Enumerable.Range(0, 5).Select(i => new
            {
                prompt = $"Q{i}",
                options = new[] { new { label = "A" } },
            }),
        });

        var result = await InvokeAsync(args);
        ErrorCode(result).Should().Be("invalid_question_count");
    }

    [Fact]
    public async Task QuestionWithNoOptions_ReturnsNoOptions()
    {
        var args = JsonSerializer.Serialize(new
        {
            context = "ctx",
            questions = new[] { new { prompt = "Q", options = Array.Empty<object>() } },
        });

        var result = await InvokeAsync(args);
        ErrorCode(result).Should().Be("no_options");
    }

    [Fact]
    public async Task DuplicateOptionValues_ReturnsDuplicateOptionValues()
    {
        var args = JsonSerializer.Serialize(new
        {
            context = "ctx",
            questions = new[]
            {
                new
                {
                    prompt = "Q",
                    options = new[] { new { label = "Red" }, new { label = "Red" } },
                },
            },
        });

        var result = await InvokeAsync(args);
        ErrorCode(result).Should().Be("duplicate_option_values");
    }

    [Fact]
    public async Task DuplicateOptionValues_ViaExplicitValueMatchingAnotherLabel_ReturnsDuplicateOptionValues()
    {
        // "value" defaults to "label" when omitted — an explicit value colliding with another
        // option's defaulted value is still a duplicate.
        var args = JsonSerializer.Serialize(new
        {
            context = "ctx",
            questions = new[]
            {
                new
                {
                    prompt = "Q",
                    options = new object[] { new { label = "Red" }, new { label = "Crimson", value = "Red" } },
                },
            },
        });

        var result = await InvokeAsync(args);
        ErrorCode(result).Should().Be("duplicate_option_values");
    }

    [Fact]
    public void Contract_DeclaresRequiredContextAndQuestionsParameters()
    {
        _tool.Contract.Parameters.Should().Contain(p => p.Name == "context" && p.IsRequired);
        _tool.Contract.Parameters.Should().Contain(p => p.Name == "questions" && p.IsRequired);
    }
}
