using System.Text.Json.Nodes;
using AchieveAi.LmDotnetTools.LmWorkflow.Binding;
using AchieveAi.LmDotnetTools.LmWorkflow.Model;
using FluentAssertions;
using Xunit;

namespace AchieveAi.LmDotnetTools.LmWorkflow.Tests;

public class StrictConditionEvaluatorTests
{
    private static readonly ConditionEvaluationPolicy Strict = ConditionEvaluationPolicy.StrictWorkflow;

    private static BindingContext CreateContext() =>
        new()
        {
            State = (JsonObject)
                JsonNode.Parse(
                    """
                    {
                      "answer": "yes",
                      "padded": " YES ",
                      "decision": true,
                      "score": 80,
                      "numericString": "80",
                      "details": {"outcome": "replied"},
                      "items": [1, 2]
                    }
                    """
                )!,
        };

    [Fact]
    public void StrictWorkflow_StringEqualityUsesOrdinalIgnoreCaseWithoutTrimming()
    {
        var context = CreateContext();

        Evaluate(Leaf(ConditionOp.Eq, "state.answer", JsonValue.Create("YES")), context).Should().BeTrue();
        Evaluate(Leaf(ConditionOp.Ne, "state.answer", JsonValue.Create("YES")), context).Should().BeFalse();
        Evaluate(Leaf(ConditionOp.Eq, "state.padded", JsonValue.Create("YES")), context).Should().BeFalse();
    }

    [Fact]
    public void StrictWorkflow_EqualityRequiresMatchingScalarTypes()
    {
        var context = CreateContext();

        Evaluate(Leaf(ConditionOp.Eq, "state.decision", JsonValue.Create(true)), context).Should().BeTrue();
        Evaluate(Leaf(ConditionOp.Eq, "state.score", JsonValue.Create(80.0)), context).Should().BeTrue();

        var booleanMismatch = () => Evaluate(Leaf(ConditionOp.Eq, "state.decision", JsonValue.Create("true")), context);
        var numberMismatch = () => Evaluate(Leaf(ConditionOp.Eq, "state.score", JsonValue.Create("80")), context);
        booleanMismatch.Should().Throw<InvalidOperationException>().WithMessage("*state.decision*Boolean*String*");
        numberMismatch.Should().Throw<InvalidOperationException>().WithMessage("*state.score*Number*String*");
        Evaluate(Leaf(ConditionOp.Eq, "state.details", JsonNode.Parse("{\"outcome\":\"replied\"}")), context)
            .Should()
            .BeTrue();
        Evaluate(Leaf(ConditionOp.Eq, "state.items", JsonNode.Parse("[1,2]")), context).Should().BeTrue();
    }

    [Fact]
    public void StrictWorkflow_OrderedComparisonRequiresNumbersWithoutStringCoercion()
    {
        var context = CreateContext();

        Evaluate(Leaf(ConditionOp.Gte, "state.score", JsonValue.Create(80)), context).Should().BeTrue();

        var numericString = () => Evaluate(Leaf(ConditionOp.Gte, "state.numericString", JsonValue.Create(80)), context);
        var orderedStrings = () => Evaluate(Leaf(ConditionOp.Lt, "state.answer", JsonValue.Create("zebra")), context);

        numericString.Should().Throw<InvalidOperationException>().WithMessage("*state.numericString*Number*String*");
        orderedStrings.Should().Throw<InvalidOperationException>().WithMessage("*state.answer*Number*String*");
    }

    [Theory]
    [InlineData(ConditionOp.Eq)]
    [InlineData(ConditionOp.Ne)]
    [InlineData(ConditionOp.Lt)]
    public void StrictWorkflow_ComparisonThrowsWhenOperandIsMissing(ConditionOp op)
    {
        var condition = Leaf(op, "state.missing", JsonValue.Create(1));

        var act = () => Evaluate(condition, CreateContext());

        act.Should().Throw<InvalidOperationException>().WithMessage("*state.missing*missing*");
    }

    [Fact]
    public void StrictWorkflow_PrevalidatesEveryCompositeLeafBeforeShortCircuiting()
    {
        var condition = new Condition
        {
            Any =
            [
                Leaf(ConditionOp.Eq, "state.decision", JsonValue.Create(true)),
                Leaf(ConditionOp.Eq, "state.missing", JsonValue.Create(true)),
            ],
        };

        var act = () => Evaluate(condition, CreateContext());

        act.Should().Throw<InvalidOperationException>().WithMessage("*state.missing*missing*");
    }

    [Fact]
    public void StrictWorkflow_EmptyOperatorsMayTestOptionalAbsence()
    {
        var context = CreateContext();

        Evaluate(Leaf(ConditionOp.Empty, "state.missing"), context).Should().BeTrue();
        Evaluate(Leaf(ConditionOp.NonEmpty, "state.missing"), context).Should().BeFalse();
    }

    [Fact]
    public void StrictWorkflow_RejectsMissingOrMistypedBindingValue()
    {
        var context = CreateContext();
        var missing = () =>
            Evaluate(Leaf(ConditionOp.Eq, "state.score", JsonValue.Create("{{state.missing}}")), context);
        var mismatch = () =>
            Evaluate(Leaf(ConditionOp.Eq, "state.score", JsonValue.Create("{{state.answer}}")), context);

        missing.Should().Throw<InvalidOperationException>().WithMessage("*state.missing*missing*");
        mismatch.Should().Throw<InvalidOperationException>().WithMessage("*state.score*Number*String*");
    }

    private static Condition Leaf(ConditionOp op, string path, JsonNode? value = null) =>
        new()
        {
            Op = op,
            Path = path,
            Value = value,
        };

    private static bool Evaluate(Condition condition, BindingContext context) =>
        ConditionEvaluator.Evaluate(condition, context, Strict);
}
