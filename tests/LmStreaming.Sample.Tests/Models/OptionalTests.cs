using System.Text.Json.Serialization;

namespace LmStreaming.Sample.Tests.Models;

public class OptionalTests
{
    // Mirrors how ASP.NET Core actually binds these DTOs (camelCase on the wire), so these tests
    // exercise the same JsonConverterFactory + naming-policy path the controllers take.
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private sealed record Payload
    {
        [JsonConverter(typeof(OptionalJsonConverterFactory))]
        public Optional<IReadOnlyList<string>?> Selection { get; init; }
    }

    [Fact]
    public void Unset_IsNotSet_AndValueIsDefault()
    {
        var optional = Optional<string>.Unset;

        optional.IsSet.Should().BeFalse();
        optional.Value.Should().BeNull();
    }

    [Fact]
    public void Constructed_IsSet_WithGivenValue()
    {
        var optional = new Optional<string>("hello");

        optional.IsSet.Should().BeTrue();
        optional.Value.Should().Be("hello");
    }

    [Fact]
    public void Deserialize_OmittedProperty_LeavesUnset()
    {
        var payload = JsonSerializer.Deserialize<Payload>("{}", Options);

        payload!.Selection.IsSet.Should().BeFalse();
    }

    [Fact]
    public void Deserialize_ExplicitNullProperty_IsSetWithNullValue()
    {
        var payload = JsonSerializer.Deserialize<Payload>("""{"selection":null}""", Options);

        payload!.Selection.IsSet.Should().BeTrue();
        payload.Selection.Value.Should().BeNull();
    }

    [Fact]
    public void Deserialize_ExplicitListProperty_IsSetWithList()
    {
        var payload = JsonSerializer.Deserialize<Payload>("""{"selection":["a","b"]}""", Options);

        payload!.Selection.IsSet.Should().BeTrue();
        payload.Selection.Value.Should().Equal("a", "b");
    }

    [Fact]
    public void Deserialize_ExplicitEmptyListProperty_IsSetWithEmptyList()
    {
        var payload = JsonSerializer.Deserialize<Payload>("""{"selection":[]}""", Options);

        payload!.Selection.IsSet.Should().BeTrue();
        payload.Selection.Value.Should().BeEmpty();
    }

    [Fact]
    public void Serialize_SetValue_WritesValue()
    {
        var payload = new Payload { Selection = new Optional<IReadOnlyList<string>?>(["a"]) };

        var json = JsonSerializer.Serialize(payload, Options);

        json.Should().Contain("\"selection\":[\"a\"]");
    }
}
