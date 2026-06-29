using System.Text.Json.Nodes;
using AchieveAi.LmDotnetTools.LmWorkflow.Binding;
using FluentAssertions;
using Xunit;

namespace AchieveAi.LmDotnetTools.LmWorkflow.Tests;

/// <summary>
///     Tests for <see cref="TemplateNodeRenderer.Render"/>: a whole-binding string leaf yields the actual
///     resolved node (type-preserving), an embedded binding yields an interpolated string, nested
///     objects/arrays render recursively, literals pass through, and absent paths follow the documented
///     null/empty rule.
/// </summary>
public class TemplateNodeRendererTests
{
    private static BindingContext Context() =>
        new()
        {
            State = (JsonObject)
                JsonNode.Parse(
                    """
                    {
                      "obj": { "k": "v", "n": 3 },
                      "arr": [1, 2, 3],
                      "num": 42,
                      "flag": true,
                      "name": "alice"
                    }
                    """
                )!,
        };

    [Fact]
    public void WholeBinding_ToObject_YieldsActualObjectNode()
    {
        var template = JsonValue.Create("  {{ state.obj }}  ");

        var rendered = TemplateNodeRenderer.Render(template, Context());

        var obj = rendered.Should().BeOfType<JsonObject>().Subject;
        obj["k"]!.GetValue<string>().Should().Be("v");
        obj["n"]!.GetValue<int>().Should().Be(3);
    }

    [Fact]
    public void WholeBinding_ToArrayAndNumber_PreservesType()
    {
        var ctx = Context();

        TemplateNodeRenderer.Render(JsonValue.Create("{{state.arr}}"), ctx)
            .Should()
            .BeOfType<JsonArray>()
            .Which.Should()
            .HaveCount(3);

        TemplateNodeRenderer.Render(JsonValue.Create("{{state.num}}"), ctx)!.GetValue<int>()
            .Should()
            .Be(42);
    }

    [Fact]
    public void EmbeddedBinding_YieldsInterpolatedString()
    {
        var rendered = TemplateNodeRenderer.Render(JsonValue.Create("v{{state.num}}"), Context());

        rendered!.GetValueKind().Should().Be(System.Text.Json.JsonValueKind.String);
        rendered.GetValue<string>().Should().Be("v42");
    }

    [Fact]
    public void NestedObjectsAndArrays_RenderRecursively()
    {
        var template = JsonNode.Parse(
            """
            {
              "whole": "{{state.obj}}",
              "list": [ "{{state.num}}", "literal", "idx-{{state.num}}" ],
              "nested": { "name": "hi {{state.name}}", "flag": "{{state.flag}}" }
            }
            """
        )!;

        var rendered = TemplateNodeRenderer.Render(template, Context())!.AsObject();

        // The whole-binding leaf becomes the actual object; the array element binding stays a number.
        rendered["whole"].Should().BeOfType<JsonObject>();
        rendered["list"]![0]!.GetValue<int>().Should().Be(42);
        rendered["list"]![1]!.GetValue<string>().Should().Be("literal");
        rendered["list"]![2]!.GetValue<string>().Should().Be("idx-42");
        rendered["nested"]!["name"]!.GetValue<string>().Should().Be("hi alice");
        // A whole-binding to a boolean preserves the boolean type.
        rendered["nested"]!["flag"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public void Literal_PassesThrough()
    {
        var template = JsonNode.Parse("""{ "a": "plain", "b": 7, "c": false }""")!;

        var rendered = TemplateNodeRenderer.Render(template, Context())!.AsObject();

        rendered["a"]!.GetValue<string>().Should().Be("plain");
        rendered["b"]!.GetValue<int>().Should().Be(7);
        rendered["c"]!.GetValue<bool>().Should().BeFalse();
    }

    [Fact]
    public void AbsentPath_WholeBinding_RendersNull_Embedded_RendersEmpty()
    {
        var ctx = Context();

        // A whole-binding to an absent path renders to JSON null.
        TemplateNodeRenderer.Render(JsonValue.Create("{{state.missing}}"), ctx).Should().BeNull();

        // An embedded binding to an absent path renders the absent value as an empty substring.
        TemplateNodeRenderer.Render(JsonValue.Create("x={{state.missing}}!"), ctx)!.GetValue<string>()
            .Should()
            .Be("x=!");
    }

    [Fact]
    public void NullTemplate_RendersNull()
    {
        TemplateNodeRenderer.Render(null, Context()).Should().BeNull();
    }
}
