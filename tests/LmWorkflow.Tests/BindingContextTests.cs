using System.Text.Json.Nodes;
using AchieveAi.LmDotnetTools.LmWorkflow.Binding;
using FluentAssertions;
using Xunit;

namespace AchieveAi.LmDotnetTools.LmWorkflow.Tests;

/// <summary>
///     Tests for <see cref="BindingContext.Resolve"/>: resolution from every root channel, nested
///     property and array-index navigation, out-of-range/absent handling, and loop-local exposure via
///     <see cref="BindingContext.WithLoop"/>.
/// </summary>
public class BindingContextTests
{
    private static BindingContext CreateContext() =>
        new()
        {
            Inputs = (JsonObject)
                JsonNode.Parse("""{ "name": "alice", "nested": { "x": 1 } }""")!,
            State = (JsonObject)JsonNode.Parse("""{ "count": 5, "items": [10, 20, 30] }""")!,
            Outputs = (JsonObject)
                JsonNode.Parse(
                    """{ "review": { "lint": [ { "severity": "high" }, { "severity": "low" } ] } }"""
                )!,
            Notes = (JsonObject)JsonNode.Parse("""{ "global": { "memo": "hello" } }""")!,
            Visits = new Dictionary<string, int> { ["nodeA"] = 3 },
            Step = 7,
        };

    [Fact]
    public void Resolve_ReadsScalarsFromEachRoot()
    {
        var ctx = CreateContext();

        ctx.Resolve("inputs.name")!.GetValue<string>().Should().Be("alice");
        ctx.Resolve("state.count")!.GetValue<int>().Should().Be(5);
        ctx.Resolve("notes.global.memo")!.GetValue<string>().Should().Be("hello");
        ctx.Resolve("step")!.GetValue<int>().Should().Be(7);
    }

    [Fact]
    public void Resolve_NavigatesNestedProperties()
    {
        var ctx = CreateContext();

        ctx.Resolve("inputs.nested.x")!.GetValue<int>().Should().Be(1);
    }

    [Fact]
    public void Resolve_NavigatesArrayIndicesAndTrailingProperties()
    {
        var ctx = CreateContext();

        ctx.Resolve("state.items[1]")!.GetValue<int>().Should().Be(20);
        ctx.Resolve("outputs.review.lint[0].severity")!.GetValue<string>().Should().Be("high");
        ctx.Resolve("outputs.review.lint[1].severity")!.GetValue<string>().Should().Be("low");
    }

    [Fact]
    public void Resolve_ReturnsNull_ForOutOfRangeIndex()
    {
        var ctx = CreateContext();

        ctx.Resolve("state.items[5]").Should().BeNull();
        ctx.Resolve("state.items[-1]").Should().BeNull();
    }

    [Fact]
    public void Resolve_ReturnsNull_ForAbsentPaths()
    {
        var ctx = CreateContext();

        ctx.Resolve("state.missing").Should().BeNull();
        ctx.Resolve("unknownRoot.x").Should().BeNull();
        ctx.Resolve("inputs.name.notAnObject").Should().BeNull();
        ctx.Resolve("").Should().BeNull();
    }

    [Fact]
    public void Resolve_ReturnsVisitCount_AndZeroWhenAbsent()
    {
        var ctx = CreateContext();

        ctx.Resolve("visits.nodeA")!.GetValue<int>().Should().Be(3);
        ctx.Resolve("visits.unknown")!.GetValue<int>().Should().Be(0);
    }

    [Fact]
    public void Resolve_ReturnsNull_ForLoopLocalsOutsideLoop()
    {
        var ctx = CreateContext();

        ctx.Resolve("item").Should().BeNull();
        ctx.Resolve("index").Should().BeNull();
        ctx.Resolve("count").Should().BeNull();
    }

    [Fact]
    public void WithLoop_ExposesItemIndexAndCount_AndSharesChannels()
    {
        var ctx = CreateContext();

        var loop = ctx.WithLoop(JsonNode.Parse("""{ "id": 42 }"""), index: 2, count: 9);

        loop.Resolve("item.id")!.GetValue<int>().Should().Be(42);
        loop.Resolve("index")!.GetValue<int>().Should().Be(2);
        loop.Resolve("count")!.GetValue<int>().Should().Be(9);

        // Shared channels remain resolvable through the loop context.
        loop.Resolve("state.count")!.GetValue<int>().Should().Be(5);
    }
}
