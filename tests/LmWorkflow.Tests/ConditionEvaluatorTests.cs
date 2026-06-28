using System.Text.Json.Nodes;
using AchieveAi.LmDotnetTools.LmWorkflow.Binding;
using AchieveAi.LmDotnetTools.LmWorkflow.Model;
using FluentAssertions;
using Xunit;

namespace AchieveAi.LmDotnetTools.LmWorkflow.Tests;

/// <summary>
///     Tests for <see cref="ConditionEvaluator.Evaluate"/>: every leaf operator, numeric vs ordinal-string
///     comparison, value-as-binding resolution, and the <c>all</c>/<c>any</c>/<c>not</c> composites
///     including nesting.
/// </summary>
public class ConditionEvaluatorTests
{
    private static BindingContext CreateContext() =>
        new()
        {
            State = (JsonObject)
                JsonNode.Parse(
                    """
                    {
                      "count": 5,
                      "name": "alice",
                      "numStr": "5",
                      "empty": "",
                      "emptyArr": [],
                      "list": [1, 2, 3],
                      "a": 5,
                      "b": 5
                    }
                    """
                )!,
        };

    private static Condition Leaf(ConditionOp op, string path, JsonNode? value = null) =>
        new()
        {
            Op = op,
            Path = path,
            Value = value,
        };

    [Fact]
    public void Eq_And_Ne_CompareNumbersNumerically()
    {
        var ctx = CreateContext();

        Evaluate(Leaf(ConditionOp.Eq, "state.count", JsonValue.Create(5)), ctx).Should().BeTrue();
        // Numeric equality ignores the integer/decimal token shape.
        Evaluate(Leaf(ConditionOp.Eq, "state.count", JsonNode.Parse("5.0")), ctx)
            .Should()
            .BeTrue();
        Evaluate(Leaf(ConditionOp.Eq, "state.count", JsonValue.Create(6)), ctx).Should().BeFalse();
        Evaluate(Leaf(ConditionOp.Ne, "state.count", JsonValue.Create(6)), ctx).Should().BeTrue();
    }

    [Fact]
    public void Eq_ComparesStringsByValue()
    {
        var ctx = CreateContext();

        Evaluate(Leaf(ConditionOp.Eq, "state.name", JsonValue.Create("alice")), ctx)
            .Should()
            .BeTrue();
        Evaluate(Leaf(ConditionOp.Eq, "state.name", JsonValue.Create("bob")), ctx)
            .Should()
            .BeFalse();
    }

    [Fact]
    public void OrderedOperators_CompareNumbers()
    {
        var ctx = CreateContext();

        Evaluate(Leaf(ConditionOp.Lt, "state.count", JsonValue.Create(10)), ctx).Should().BeTrue();
        Evaluate(Leaf(ConditionOp.Lte, "state.count", JsonValue.Create(5)), ctx).Should().BeTrue();
        Evaluate(Leaf(ConditionOp.Gt, "state.count", JsonValue.Create(3)), ctx).Should().BeTrue();
        Evaluate(Leaf(ConditionOp.Gte, "state.count", JsonValue.Create(6)), ctx).Should().BeFalse();
    }

    [Fact]
    public void OrderedOperators_TreatNumericStringsAsNumbers()
    {
        var ctx = CreateContext();

        // state.numStr is the string "5"; it parses as a number so the compare is numeric, not ordinal.
        Evaluate(Leaf(ConditionOp.Lt, "state.numStr", JsonValue.Create(10)), ctx)
            .Should()
            .BeTrue();
    }

    [Fact]
    public void OrderedOperators_FallBackToOrdinalStringComparison()
    {
        var ctx = CreateContext();

        Evaluate(Leaf(ConditionOp.Lt, "state.name", JsonValue.Create("bob")), ctx)
            .Should()
            .BeTrue();
        Evaluate(Leaf(ConditionOp.Gt, "state.name", JsonValue.Create("bob")), ctx)
            .Should()
            .BeFalse();
    }

    [Fact]
    public void OrderedOperators_ReturnFalse_WhenOperandMissing()
    {
        var ctx = CreateContext();

        Evaluate(Leaf(ConditionOp.Lt, "state.missing", JsonValue.Create(10)), ctx)
            .Should()
            .BeFalse();
    }

    [Fact]
    public void In_TestsMembership_BothDirections()
    {
        var ctx = CreateContext();

        // Value is an array: left path value must be one of its elements.
        Evaluate(Leaf(ConditionOp.In, "state.count", JsonNode.Parse("[1, 5, 9]")), ctx)
            .Should()
            .BeTrue();
        Evaluate(Leaf(ConditionOp.In, "state.count", JsonNode.Parse("[1, 2, 3]")), ctx)
            .Should()
            .BeFalse();

        // Symmetric: left path is an array, value is a scalar member.
        Evaluate(Leaf(ConditionOp.In, "state.list", JsonValue.Create(2)), ctx).Should().BeTrue();
        Evaluate(Leaf(ConditionOp.In, "state.list", JsonValue.Create(9)), ctx).Should().BeFalse();
    }

    [Fact]
    public void Empty_And_NonEmpty_CoverNullStringArrayObject()
    {
        var ctx = CreateContext();

        Evaluate(Leaf(ConditionOp.Empty, "state.empty"), ctx).Should().BeTrue();
        Evaluate(Leaf(ConditionOp.Empty, "state.emptyArr"), ctx).Should().BeTrue();
        Evaluate(Leaf(ConditionOp.Empty, "state.missing"), ctx).Should().BeTrue();
        Evaluate(Leaf(ConditionOp.Empty, "state.name"), ctx).Should().BeFalse();

        Evaluate(Leaf(ConditionOp.NonEmpty, "state.name"), ctx).Should().BeTrue();
        Evaluate(Leaf(ConditionOp.NonEmpty, "state.empty"), ctx).Should().BeFalse();
    }

    [Fact]
    public void Value_AsBinding_IsResolvedThroughContext()
    {
        var ctx = CreateContext();

        // state.a == state.b, resolved by treating the value string as a binding.
        Evaluate(Leaf(ConditionOp.Eq, "state.a", JsonValue.Create("{{state.b}}")), ctx)
            .Should()
            .BeTrue();
        Evaluate(Leaf(ConditionOp.Eq, "state.a", JsonValue.Create("{{state.count}}")), ctx)
            .Should()
            .BeTrue();
        Evaluate(Leaf(ConditionOp.Eq, "state.a", JsonValue.Create("{{state.missing}}")), ctx)
            .Should()
            .BeFalse();
    }

    [Fact]
    public void Composite_All_RequiresEveryChild()
    {
        var ctx = CreateContext();

        var allTrue = new Condition
        {
            All =
            [
                Leaf(ConditionOp.Eq, "state.count", JsonValue.Create(5)),
                Leaf(ConditionOp.NonEmpty, "state.name"),
            ],
        };
        var oneFalse = new Condition
        {
            All =
            [
                Leaf(ConditionOp.Eq, "state.count", JsonValue.Create(5)),
                Leaf(ConditionOp.Eq, "state.count", JsonValue.Create(99)),
            ],
        };

        Evaluate(allTrue, ctx).Should().BeTrue();
        Evaluate(oneFalse, ctx).Should().BeFalse();
    }

    [Fact]
    public void Composite_Any_RequiresAtLeastOneChild()
    {
        var ctx = CreateContext();

        var oneTrue = new Condition
        {
            Any =
            [
                Leaf(ConditionOp.Eq, "state.count", JsonValue.Create(99)),
                Leaf(ConditionOp.Eq, "state.count", JsonValue.Create(5)),
            ],
        };
        var noneTrue = new Condition
        {
            Any =
            [
                Leaf(ConditionOp.Eq, "state.count", JsonValue.Create(98)),
                Leaf(ConditionOp.Eq, "state.count", JsonValue.Create(99)),
            ],
        };

        Evaluate(oneTrue, ctx).Should().BeTrue();
        Evaluate(noneTrue, ctx).Should().BeFalse();
    }

    [Fact]
    public void Composite_Not_NegatesChild_AndNests()
    {
        var ctx = CreateContext();

        var not = new Condition { Not = Leaf(ConditionOp.Eq, "state.count", JsonValue.Create(99)) };
        Evaluate(not, ctx).Should().BeTrue();

        // all[ any[false, true], not(false) ] -> true
        var nested = new Condition
        {
            All =
            [
                new Condition
                {
                    Any =
                    [
                        Leaf(ConditionOp.Eq, "state.count", JsonValue.Create(99)),
                        Leaf(ConditionOp.Gt, "state.count", JsonValue.Create(1)),
                    ],
                },
                new Condition
                {
                    Not = Leaf(ConditionOp.Empty, "state.name"),
                },
            ],
        };
        Evaluate(nested, ctx).Should().BeTrue();
    }

    private static bool Evaluate(Condition condition, BindingContext ctx) =>
        ConditionEvaluator.Evaluate(condition, ctx);
}
