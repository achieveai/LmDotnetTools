using System.Text.Json;
using AchieveAi.LmDotnetTools.LmWorkflow.Binding;
using AchieveAi.LmDotnetTools.LmWorkflow.Model;
using FluentAssertions;
using Xunit;

namespace AchieveAi.LmDotnetTools.LmWorkflow.Tests;

public class NaturalConditionParserTests
{
    public static TheoryData<string, ConditionOp, JsonValueKind, string> ValidExpressions =>
        new()
        {
            { "state.Result.Decision == true", ConditionOp.Eq, JsonValueKind.True, "true" },
            { "state.Result.Decision != false", ConditionOp.Ne, JsonValueKind.False, "false" },
            { "state.Grade.Score < 80", ConditionOp.Lt, JsonValueKind.Number, "80" },
            { "state.Grade.Score <= -12.5", ConditionOp.Lte, JsonValueKind.Number, "-12.5" },
            { "state.Grade.Score > 6.02e23", ConditionOp.Gt, JsonValueKind.Number, "6.02e23" },
            { "state.Grade.Score >= 1E-3", ConditionOp.Gte, JsonValueKind.Number, "1E-3" },
            { "state.Result.Answer == 'YES'", ConditionOp.Eq, JsonValueKind.String, "YES" },
            { "state.Result.Answer == \"YES\"", ConditionOp.Eq, JsonValueKind.String, "YES" },
        };

    [Theory]
    [MemberData(nameof(ValidExpressions))]
    public void Parse_LowersSupportedExpressionToCondition(
        string expression,
        ConditionOp expectedOperator,
        JsonValueKind expectedKind,
        string expectedText
    )
    {
        var condition = NaturalConditionParser.Parse(expression);

        condition
            .Path.Should()
            .Be(expression.Split([' ', '=', '!', '<', '>'], StringSplitOptions.RemoveEmptyEntries)[0]);
        condition.Op.Should().Be(expectedOperator);
        condition.Value.Should().NotBeNull();
        condition.Value!.GetValueKind().Should().Be(expectedKind);
        ValueText(condition).Should().Be(expectedText);
    }

    [Fact]
    public void Parse_PreservesIndexedPathAndIgnoresExpressionWhitespace()
    {
        var condition = NaturalConditionParser.Parse("  state.Review.Findings[0].Severity   ==   'high'  ");

        condition.Path.Should().Be("state.Review.Findings[0].Severity");
        condition.Op.Should().Be(ConditionOp.Eq);
        condition.Value!.GetValue<string>().Should().Be("high");
    }

    [Fact]
    public void Parse_DecodesSupportedStringEscapes()
    {
        NaturalConditionParser
            .Parse("state.message == 'reviewer''s note'")
            .Value!.GetValue<string>()
            .Should()
            .Be("reviewer's note");
        NaturalConditionParser
            .Parse("state.message == \"line\\n\\u263A\"")
            .Value!.GetValue<string>()
            .Should()
            .Be("line\n☺");
    }

    [Theory]
    [InlineData("")]
    [InlineData("state.Result.Decision")]
    [InlineData("state.Result.Decision = true")]
    [InlineData("state.Result.Decision === true")]
    [InlineData("state.Result.Decision == null")]
    [InlineData("state.Result.Decision == state.Other.Decision")]
    [InlineData("state.Result.Decision == true && state.Other == false")]
    [InlineData("state.Result.Decision == {{inputs.expected}}")]
    [InlineData("state.Result.Decision == true trailing")]
    [InlineData("state..Result.Decision == true")]
    [InlineData("state.Result[abc].Decision == true")]
    [InlineData("state.Result.Decision == 'unterminated")]
    [InlineData("state.Result.Decision == \"bad\\xescape\"")]
    [InlineData("state.Result.Score == 01")]
    [InlineData("state.Result.Score == 1e999")]
    public void Parse_RejectsUnsupportedOrMalformedSyntax(string expression)
    {
        var act = () => NaturalConditionParser.Parse(expression);

        act.Should().Throw<FormatException>();
    }

    [Fact]
    public void Parse_RejectsNullExpression()
    {
        var act = () => NaturalConditionParser.Parse(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    private static string ValueText(Condition condition) =>
        condition.Value!.GetValueKind() == JsonValueKind.String
            ? condition.Value.GetValue<string>()
            : condition.Value.ToJsonString();
}
