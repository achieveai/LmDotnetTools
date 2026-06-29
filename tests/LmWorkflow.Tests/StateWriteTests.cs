using System.Text.Json.Nodes;
using AchieveAi.LmDotnetTools.LmWorkflow.Binding;
using AchieveAi.LmDotnetTools.LmWorkflow.Model;
using FluentAssertions;
using Xunit;

namespace AchieveAi.LmDotnetTools.LmWorkflow.Tests;

/// <summary>
///     Tests for <see cref="StateWriter.Apply"/>: the <c>set</c>/<c>append</c>/<c>merge</c> modes, the
///     <see cref="WriteSpec.From"/> projection, intermediate-path creation, and append-to-absent.
/// </summary>
public class StateWriteTests
{
    private static JsonObject State(string json) => (JsonObject)JsonNode.Parse(json)!;

    private static int[] IntArray(JsonNode? node) =>
        [.. node!.AsArray().Select(element => element!.GetValue<int>())];

    [Fact]
    public void Set_OverwritesDestination()
    {
        var state = State("""{ "x": 1 }""");

        StateWriter.Apply(state, new WriteSpec { To = "state.x", Mode = WriteMode.Set }, JsonValue.Create(2));

        state["x"]!.GetValue<int>().Should().Be(2);
    }

    [Fact]
    public void Append_PushesScalarOntoExistingArray()
    {
        var state = State("""{ "arr": [1] }""");

        StateWriter.Apply(
            state,
            new WriteSpec { To = "state.arr", Mode = WriteMode.Append },
            JsonValue.Create(2)
        );

        IntArray(state["arr"]).Should().Equal(1, 2);
    }

    [Fact]
    public void Append_ConcatenatesWhenSourceIsArray()
    {
        var state = State("""{ "arr": [1] }""");

        StateWriter.Apply(
            state,
            new WriteSpec { To = "state.arr", Mode = WriteMode.Append },
            JsonNode.Parse("[2, 3]")
        );

        // Concatenated (spread), NOT nested as [1, [2, 3]].
        IntArray(state["arr"]).Should().Equal(1, 2, 3);
    }

    [Fact]
    public void Append_CreatesSingletonArray_WhenDestinationAbsent()
    {
        var state = State("{}");

        StateWriter.Apply(
            state,
            new WriteSpec { To = "state.arr", Mode = WriteMode.Append },
            JsonValue.Create(5)
        );

        IntArray(state["arr"]).Should().Equal(5);
    }

    [Fact]
    public void Merge_ShallowMergesObjectsOverwritingOverlappingKeys()
    {
        var state = State("""{ "obj": { "a": 1, "b": 2 } }""");

        StateWriter.Apply(
            state,
            new WriteSpec { To = "state.obj", Mode = WriteMode.Merge },
            JsonNode.Parse("""{ "b": 9, "c": 3 }""")
        );

        var merged = state["obj"]!.AsObject();
        merged["a"]!.GetValue<int>().Should().Be(1);
        merged["b"]!.GetValue<int>().Should().Be(9);
        merged["c"]!.GetValue<int>().Should().Be(3);
    }

    [Fact]
    public void From_ProjectsSubpathOfOutput()
    {
        var state = State("{}");
        var output = JsonNode.Parse("""{ "summary": "text", "other": 1 }""");

        StateWriter.Apply(
            state,
            new WriteSpec { From = "summary", To = "state.s", Mode = WriteMode.Set },
            output
        );

        state["s"]!.GetValue<string>().Should().Be("text");
    }

    [Fact]
    public void Set_CreatesMissingIntermediateObjects()
    {
        var state = State("{}");

        StateWriter.Apply(
            state,
            new WriteSpec { To = "state.a.b", Mode = WriteMode.Set },
            JsonValue.Create(7)
        );

        state["a"].Should().BeOfType<JsonObject>();
        state["a"]!["b"]!.GetValue<int>().Should().Be(7);
    }

    [Fact]
    public void Apply_Throws_ForUpsertMode()
    {
        var state = State("{}");

        var act = () =>
            StateWriter.Apply(
                state,
                new WriteSpec { To = "state.x", Mode = WriteMode.Upsert },
                JsonValue.Create(1)
            );

        act.Should().Throw<NotSupportedException>();
    }

    [Fact]
    public void Apply_Throws_WhenDestinationNotUnderState()
    {
        var state = State("{}");

        var act = () =>
            StateWriter.Apply(
                state,
                new WriteSpec { To = "outputs.x", Mode = WriteMode.Set },
                JsonValue.Create(1)
            );

        act.Should().Throw<ArgumentException>();
    }
}
