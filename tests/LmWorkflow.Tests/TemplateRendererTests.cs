using System.Text.Json.Nodes;
using AchieveAi.LmDotnetTools.LmWorkflow.Binding;
using FluentAssertions;
using Xunit;

namespace AchieveAi.LmDotnetTools.LmWorkflow.Tests;

/// <summary>
///     Tests for <see cref="TemplateRenderer.Render"/>: scalar substitution, compact-JSON rendering of
///     objects/arrays, multiple substitutions, absent-binding handling, whitespace tolerance, and the
///     object/array size guard.
/// </summary>
public class TemplateRendererTests
{
    private static BindingContext CreateContext() =>
        new()
        {
            Inputs = (JsonObject)JsonNode.Parse("""{ "name": "alice" }""")!,
            State = (JsonObject)
                JsonNode.Parse("""{ "count": 5, "obj": { "a": 1, "b": 2 } }""")!,
        };

    [Fact]
    public void Render_SubstitutesScalarValues()
    {
        var ctx = CreateContext();

        TemplateRenderer.Render("Hello {{inputs.name}}!", ctx).Should().Be("Hello alice!");
        TemplateRenderer.Render("count={{state.count}}", ctx).Should().Be("count=5");
    }

    [Fact]
    public void Render_RendersObjectsAsCompactJson()
    {
        var ctx = CreateContext();

        TemplateRenderer.Render("{{state.obj}}", ctx).Should().Be("""{"a":1,"b":2}""");
    }

    [Fact]
    public void Render_SupportsMultipleSubstitutionsInOneTemplate()
    {
        var ctx = CreateContext();

        TemplateRenderer
            .Render("n={{state.count}} who={{inputs.name}}", ctx)
            .Should()
            .Be("n=5 who=alice");
    }

    [Fact]
    public void Render_EmitsEmptyString_ForAbsentBinding()
    {
        var ctx = CreateContext();

        TemplateRenderer.Render("v=[{{state.missing}}]", ctx).Should().Be("v=[]");
    }

    [Fact]
    public void Render_ToleratesWhitespaceInsideBraces()
    {
        var ctx = CreateContext();

        TemplateRenderer.Render("{{   state.count   }}", ctx).Should().Be("5");
    }

    [Fact]
    public void Render_PassesThroughTextWithoutBindings()
    {
        var ctx = CreateContext();

        TemplateRenderer.Render("no bindings here", ctx).Should().Be("no bindings here");
    }

    [Fact]
    public void Render_TruncatesObjectRendering_PastMaxBindingBytes()
    {
        var array = new JsonArray();
        for (var i = 0; i < 4000; i++)
        {
            array.Add(i);
        }

        var ctx = new BindingContext { State = new JsonObject { ["big"] = array } };
        var full = ctx.Resolve("state.big")!.ToJsonString();
        full.Length.Should().BeGreaterThan(TemplateRenderer.MaxBindingBytes);

        var rendered = TemplateRenderer.Render("{{state.big}}", ctx);

        rendered.Length.Should().BeLessThan(full.Length);
        rendered.Should().StartWith(full[..TemplateRenderer.MaxBindingBytes]);
        rendered.Should().Contain("[truncated");
        rendered.Should().EndWith("bytes]");
    }
}
