using System.Text.Json;
using AchieveAi.LmDotnetTools.GithubCopilotProvider.Models;
using FluentAssertions;

namespace AchieveAi.LmDotnetTools.GithubCopilotProvider.Tests.Models;

/// <summary>
///     Directly exercises the shared <see cref="CopilotModelsResponse"/> primitives that
///     <see cref="CopilotModelCatalogParser"/> and the CopilotAnthropicProxy resolver both build on —
///     response unwrap, endpoint-spelling constants, <c>supported_endpoints</c> reads, and string reads —
///     across the malformed/partial shapes both callers must tolerate.
/// </summary>
public sealed class CopilotModelsResponseTests
{
    // Each helper takes a JsonElement view over a JsonDocument the caller owns; parse inside the test
    // and read within scope so the element stays valid (mirrors how both real callers use it).
    private static JsonElement Root(string json)
    {
        return JsonDocument.Parse(json).RootElement;
    }

    [Fact]
    public void Endpoint_constants_use_the_exact_wire_spellings()
    {
        CopilotModelsResponse.MessagesEndpoint.Should().Be("/v1/messages");
        CopilotModelsResponse.ResponsesEndpoint.Should().Be("/responses");
        CopilotModelsResponse.ResponsesWebSocketEndpoint.Should().Be("ws:/responses");
    }

    [Fact]
    public void EnumerateModelEntries_unwraps_data_envelope()
    {
        const string json = """{ "data": [ { "id": "a" }, { "id": "b" } ] }""";

        var ids = CopilotModelsResponse.EnumerateModelEntries(Root(json))
            .Select(e => CopilotModelsResponse.GetString(e, "id"))
            .ToList();

        ids.Should().Equal("a", "b");
    }

    [Fact]
    public void EnumerateModelEntries_accepts_bare_array()
    {
        const string json = """[ { "id": "a" }, { "id": "b" } ]""";

        CopilotModelsResponse.EnumerateModelEntries(Root(json)).Should().HaveCount(2);
    }

    [Fact]
    public void EnumerateModelEntries_skips_non_object_entries()
    {
        const string json = """[ { "id": "a" }, 5, "s", null, { "id": "b" } ]""";

        var ids = CopilotModelsResponse.EnumerateModelEntries(Root(json))
            .Select(e => CopilotModelsResponse.GetString(e, "id"))
            .ToList();

        ids.Should().Equal("a", "b");
    }

    [Theory]
    [InlineData("{}")]                       // object, no data property
    [InlineData("""{ "data": {} }""")]      // data is an object, not an array
    [InlineData("""{ "data": 123 }""")]     // data is a scalar
    [InlineData("""{ "data": null }""")]    // data is null
    [InlineData("123")]                       // bare scalar root
    [InlineData("\"hello\"")]                // bare string root
    [InlineData("""{ "data": [] }""")]      // empty list
    public void EnumerateModelEntries_yields_nothing_for_non_list_shapes(string json)
    {
        CopilotModelsResponse.EnumerateModelEntries(Root(json)).Should().BeEmpty();
    }

    [Theory]
    [InlineData("""{ "supported_endpoints": ["/v1/messages"] }""")] // present, array
    [InlineData("""{ "supported_endpoints": null }""")]              // present, null
    [InlineData("""{ "supported_endpoints": "x" }""")]              // present, string
    public void HasSupportedEndpoints_is_true_whenever_property_present(string json)
    {
        CopilotModelsResponse.HasSupportedEndpoints(Root(json)).Should().BeTrue();
    }

    [Fact]
    public void HasSupportedEndpoints_is_false_when_absent()
    {
        CopilotModelsResponse.HasSupportedEndpoints(Root("""{ "id": "a" }""")).Should().BeFalse();
    }

    [Fact]
    public void SupportsEndpoint_matches_exact_spelling_case_insensitively()
    {
        var item = Root("""{ "supported_endpoints": ["/V1/Messages"] }""");

        CopilotModelsResponse.SupportsEndpoint(item, CopilotModelsResponse.MessagesEndpoint).Should().BeTrue();
    }

    [Fact]
    public void SupportsEndpoint_distinguishes_transports()
    {
        var item = Root("""{ "supported_endpoints": ["/responses", "ws:/responses"] }""");

        CopilotModelsResponse.SupportsEndpoint(item, CopilotModelsResponse.MessagesEndpoint).Should().BeFalse();
        CopilotModelsResponse.SupportsEndpoint(item, CopilotModelsResponse.ResponsesEndpoint).Should().BeTrue();
        CopilotModelsResponse.SupportsEndpoint(item, CopilotModelsResponse.ResponsesWebSocketEndpoint).Should().BeTrue();
    }

    [Fact]
    public void SupportsEndpoint_does_not_match_a_similar_but_unequal_endpoint()
    {
        // A superstring must not count as a match — endpoint comparison is exact, not prefix/contains.
        var item = Root("""{ "supported_endpoints": ["/v1/messages/count_tokens"] }""");

        CopilotModelsResponse.SupportsEndpoint(item, CopilotModelsResponse.MessagesEndpoint).Should().BeFalse();
    }

    [Fact]
    public void SupportsEndpoint_skips_non_string_values_but_still_finds_the_match()
    {
        var item = Root("""{ "supported_endpoints": [123, true, "/v1/messages"] }""");

        CopilotModelsResponse.SupportsEndpoint(item, CopilotModelsResponse.MessagesEndpoint).Should().BeTrue();
    }

    [Theory]
    [InlineData("""{ "id": "a" }""")]                       // property absent
    [InlineData("""{ "supported_endpoints": "x" }""")]     // property present but not an array
    [InlineData("""{ "supported_endpoints": [123] }""")]   // array of non-strings only
    public void SupportsEndpoint_is_false_for_absent_or_malformed_metadata(string json)
    {
        CopilotModelsResponse.SupportsEndpoint(Root(json), CopilotModelsResponse.MessagesEndpoint).Should().BeFalse();
    }

    [Fact]
    public void GetSupportedEndpoints_returns_undefined_when_absent_or_not_array()
    {
        CopilotModelsResponse.GetSupportedEndpoints(Root("""{ "id": "a" }""")).ValueKind
            .Should().Be(JsonValueKind.Undefined);
        CopilotModelsResponse.GetSupportedEndpoints(Root("""{ "supported_endpoints": "x" }""")).ValueKind
            .Should().Be(JsonValueKind.Undefined);
    }

    [Fact]
    public void GetString_reads_present_string_property()
    {
        CopilotModelsResponse.GetString(Root("""{ "id": "claude-opus-4.8" }"""), "id")
            .Should().Be("claude-opus-4.8");
    }

    [Theory]
    [InlineData("""{ "id": 123 }""")]     // present but not a string
    [InlineData("""{ "name": "x" }""")]   // different property absent
    [InlineData("[]")]                       // not an object at all
    public void GetString_returns_null_for_missing_non_string_or_non_object(string json)
    {
        CopilotModelsResponse.GetString(Root(json), "id").Should().BeNull();
    }
}
