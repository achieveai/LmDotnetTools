using AchieveAi.LmDotnetTools.GithubCopilotProvider.Models;
using FluentAssertions;

namespace AchieveAi.LmDotnetTools.GithubCopilotProvider.Tests.Models;

public sealed class CopilotModelCatalogParserTests
{
    private static string RealResponseJson =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "copilot-models-real-response.json"));

    [Fact]
    public void Parse_real_response_keeps_only_routable_anthropic_and_openai_models()
    {
        var models = CopilotModelCatalogParser.Parse(RealResponseJson);

        // 34 models upstream → 13 routable Anthropic/OpenAI (7 Claude + 5 OpenAI + 1 Azure-OpenAI).
        models.Should().HaveCount(13);
        models
            .Should()
            .OnlyContain(m => m.Vendor == CopilotModelVendor.Anthropic || m.Vendor == CopilotModelVendor.OpenAI);
        models
            .Should()
            .OnlyContain(m =>
                m.Transport == CopilotModelTransport.Anthropic || m.Transport == CopilotModelTransport.Responses
            );
    }

    [Fact]
    public void Parse_maps_claude_models_to_anthropic_partition_with_messages_transport()
    {
        var models = CopilotModelCatalogParser.Parse(RealResponseJson);

        var anthropic = models.Where(m => m.Vendor == CopilotModelVendor.Anthropic).ToList();

        anthropic.Should().HaveCount(7);
        anthropic.Should().OnlyContain(m => m.Transport == CopilotModelTransport.Anthropic);
        anthropic.Select(m => m.Id).Should().Contain(["claude-opus-4.8", "claude-sonnet-5", "claude-haiku-4.5"]);
    }

    [Fact]
    public void Parse_maps_gpt_models_to_openai_partition_with_responses_transport()
    {
        var models = CopilotModelCatalogParser.Parse(RealResponseJson);

        var openAi = models.Where(m => m.Vendor == CopilotModelVendor.OpenAI).ToList();

        openAi.Should().HaveCount(6);
        openAi.Should().OnlyContain(m => m.Transport == CopilotModelTransport.Responses);
        openAi.Select(m => m.Id).Should().Contain("gpt-5.5");
    }

    [Fact]
    public void Parse_normalizes_azure_openai_vendor_to_openai()
    {
        var models = CopilotModelCatalogParser.Parse(RealResponseJson);

        // gpt-5-mini is reported as vendor "Azure OpenAI" in the real response.
        var mini = models.SingleOrDefault(m => m.Id == "gpt-5-mini");

        mini.Should().NotBeNull();
        mini!.Vendor.Should().Be(CopilotModelVendor.OpenAI);
    }

    [Fact]
    public void Parse_excludes_google_and_chat_completions_only_models()
    {
        var models = CopilotModelCatalogParser.Parse(RealResponseJson);

        models.Select(m => m.Id).Should().NotContain(["gemini-3.1-pro-preview", "gemini-3.5-flash", "gemini-2.5-pro"]);
    }

    [Fact]
    public void Parse_excludes_non_partition_vendors_even_when_transport_is_routable()
    {
        var models = CopilotModelCatalogParser.Parse(RealResponseJson);

        // mai-code-1-flash-picker supports /responses but is vendor "Microsoft" — vendor filtering
        // is independent of transport, so it must not appear.
        models.Select(m => m.Id).Should().NotContain("mai-code-1-flash-picker");
    }

    [Fact]
    public void Parse_uses_name_as_display_name()
    {
        var models = CopilotModelCatalogParser.Parse(RealResponseJson);

        var opus = models.Single(m => m.Id == "claude-opus-4.6");

        opus.DisplayName.Should().Be("Claude Opus 4.6");
    }

    [Fact]
    public void Parse_flags_adaptive_thinking_from_capabilities()
    {
        var models = CopilotModelCatalogParser.Parse(RealResponseJson);

        // Newer Claude models advertise capabilities.supports.adaptive_thinking=true and reject the
        // classic thinking.type.enabled budget API; the older ones don't advertise it.
        models.Single(m => m.Id == "claude-sonnet-5").SupportsAdaptiveThinking.Should().BeTrue();
        models.Single(m => m.Id == "claude-opus-4.8").SupportsAdaptiveThinking.Should().BeTrue();
        models.Single(m => m.Id == "claude-sonnet-4.5").SupportsAdaptiveThinking.Should().BeFalse();
        models.Single(m => m.Id == "claude-haiku-4.5").SupportsAdaptiveThinking.Should().BeFalse();
    }

    [Theory]
    [InlineData(
        """["none", "minimal", "low", "medium", "high", "xhigh", "max"]""",
        new[] { "none", "minimal", "low", "medium", "high", "xhigh", "max" }
    )]
    [InlineData("""["low", 42, null, "high"]""", new[] { "low", "high" })]
    [InlineData("null", new string[0])]
    public void Parse_projects_reasoning_effort_values(string reasoningEffortJson, string[] expected)
    {
        var json = $$"""
            { "data": [{
              "id": "claude-test",
              "vendor": "Anthropic",
              "supported_endpoints": ["/v1/messages"],
              "capabilities": {
                "supports": {
                  "reasoning_effort": {{reasoningEffortJson}}
                }
              }
            }] }
            """;

        var model = CopilotModelCatalogParser.Parse(json).Should().ContainSingle().Subject;

        model.ReasoningEfforts.Should().Equal(expected);
    }

    [Fact]
    public void Parse_equivalent_models_have_value_equality()
    {
        const string json = """
            { "data": [{
              "id": "gpt-test",
              "name": "GPT Test",
              "vendor": "OpenAI",
              "supported_endpoints": ["/responses"],
              "capabilities": {
                "supports": {
                  "reasoning_effort": ["low", "medium", "high"]
                }
              }
            }] }
            """;

        var first = CopilotModelCatalogParser.Parse(json).Should().ContainSingle().Subject;
        var second = CopilotModelCatalogParser.Parse(json).Should().ContainSingle().Subject;

        first.Should().Be(second);
        first.GetHashCode().Should().Be(second.GetHashCode());
    }

    [Fact]
    public void CopilotModelInfo_does_not_expose_mutable_reasoning_efforts()
    {
        string[] source = ["low", "medium"];
        var model = new CopilotModelInfo(
            "gpt-test",
            "GPT Test",
            CopilotModelVendor.OpenAI,
            CopilotModelTransport.Responses
        )
        {
            ReasoningEfforts = source,
        };
        var efforts = model.ReasoningEfforts.Should().BeAssignableTo<IList<string>>().Subject;

        var mutate = () => efforts[0] = "high";

        mutate.Should().Throw<NotSupportedException>();
        source[1] = "xhigh";
        model.ReasoningEfforts.Should().Equal("low", "medium");
    }

    [Fact]
    public void CopilotModelInfo_reasoning_efforts_do_not_change_legacy_equality_or_hash_code()
    {
        var legacy = new CopilotModelInfo(
            "gpt-test",
            "GPT Test",
            CopilotModelVendor.OpenAI,
            CopilotModelTransport.Responses
        );
        var enriched = legacy with { ReasoningEfforts = ["low", "medium", "high"] };

        enriched.Should().Be(legacy);
        enriched.GetHashCode().Should().Be(legacy.GetHashCode());
    }

    [Fact]
    public void CopilotModelInfo_NullReasoningEffortsNormalizesToEmpty()
    {
        var model = new CopilotModelInfo(
            "gpt-test",
            "GPT Test",
            CopilotModelVendor.OpenAI,
            CopilotModelTransport.Responses)
        {
            ReasoningEfforts = null!,
        };

        model.ReasoningEfforts.Should().BeEmpty();
    }

    [Fact]
    public void CopilotModelInfo_preserves_positional_constructor_compatibility()
    {
        var model = new CopilotModelInfo(
            "gpt-test",
            "GPT Test",
            CopilotModelVendor.OpenAI,
            CopilotModelTransport.Responses,
            SupportsAdaptiveThinking: true
        );

        model.SupportsAdaptiveThinking.Should().BeTrue();
        model.ReasoningEfforts.Should().BeEmpty();
    }

    [Fact]
    public void Parse_accepts_bare_array_shape()
    {
        const string json = """
            [
              { "id": "claude-sonnet-5", "name": "Claude Sonnet 5", "vendor": "Anthropic", "supported_endpoints": ["/v1/messages"] },
              { "id": "gpt-5.5", "name": "GPT-5.5", "vendor": "OpenAI", "supported_endpoints": ["/responses"] }
            ]
            """;

        var models = CopilotModelCatalogParser.Parse(json);

        models.Should().HaveCount(2);
    }

    [Fact]
    public void Parse_treats_responses_websocket_endpoint_as_responses_transport()
    {
        const string json = """
            { "data": [
              { "id": "gpt-x", "name": "GPT X", "vendor": "OpenAI", "supported_endpoints": ["ws:/responses"] }
            ] }
            """;

        var models = CopilotModelCatalogParser.Parse(json);

        models.Should().ContainSingle().Which.Transport.Should().Be(CopilotModelTransport.Responses);
    }

    [Fact]
    public void Parse_skips_entries_without_supported_endpoints_metadata()
    {
        // Endpoint metadata is required to decide a transport; an entry lacking it can't be routed.
        const string json = """
            { "data": [
              { "id": "mystery", "name": "Mystery", "vendor": "OpenAI" }
            ] }
            """;

        var models = CopilotModelCatalogParser.Parse(json);

        models.Should().BeEmpty();
    }

    [Fact]
    public void Parse_falls_back_to_id_when_name_missing()
    {
        const string json = """
            { "data": [
              { "id": "claude-sonnet-5", "vendor": "Anthropic", "supported_endpoints": ["/v1/messages"] }
            ] }
            """;

        var models = CopilotModelCatalogParser.Parse(json);

        models.Should().ContainSingle().Which.DisplayName.Should().Be("claude-sonnet-5");
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{ "data": {} }""")]
    [InlineData("""{ "data": [] }""")]
    public void Parse_returns_empty_for_missing_or_empty_lists(string json)
    {
        CopilotModelCatalogParser.Parse(json).Should().BeEmpty();
    }
}
