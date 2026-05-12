using System.Text;
using AchieveAi.LmDotnetTools.OpenAIProvider.Agents;
using AchieveAi.LmDotnetTools.OpenAIProvider.Models;
using FluentAssertions;

namespace AchieveAi.LmDotnetTools.MockProviderHost.Tests;

/// <summary>
/// Verifies <see cref="JsonScenarioLoader"/> parses well-formed scenarios into a usable
/// <c>ScriptedSseResponder</c> and rejects malformed shapes with a useful diagnostic.
/// </summary>
public sealed class JsonScenarioLoaderTests
{
    [Fact]
    public void Load_resolves_the_embedded_demo_scenario()
    {
        var responder = JsonScenarioLoader.Load("demo");

        // The shipped scenario has one role ("demo") with three turns.
        responder.RemainingTurns.Should().ContainKey("demo").WhoseValue.Should().Be(3);
    }

    [Fact]
    public void ListBuiltinScenarios_contains_demo()
    {
        JsonScenarioLoader.ListBuiltinScenarios().Should().Contain("demo");
    }

    [Fact]
    public void Parse_supports_all_match_types()
    {
        const string json = """
            {
              "roles": [
                { "key": "alpha", "match": { "type": "always" }, "turns": [
                    { "messages": [{ "kind": "text", "text": "a" }] }
                ]},
                { "key": "beta", "match": { "type": "system_contains", "value": "marker" }, "turns": [
                    { "messages": [{ "kind": "text", "text": "b" }] }
                ]},
                { "key": "gamma", "match": { "type": "user_contains", "value": "hello" }, "turns": [
                    { "messages": [{ "kind": "text", "text": "c" }] }
                ]},
                { "key": "delta", "match": { "type": "tool", "name": "echo" }, "turns": [
                    { "messages": [{ "kind": "text", "text": "d" }] }
                ]},
                { "key": "epsilon", "match": { "type": "tool_result" }, "turns": [
                    { "messages": [{ "kind": "text", "text": "e" }] }
                ]}
              ]
            }
            """;

        var responder = JsonScenarioLoader.Parse(json);

        responder.RemainingTurns.Keys.Should().BeEquivalentTo(["alpha", "beta", "gamma", "delta", "epsilon"]);
    }

    [Fact]
    public void Parse_supports_all_message_kinds_and_thinking()
    {
        const string json = """
            {
              "roles": [{
                "key": "demo", "match": { "type": "always" }, "turns": [
                  { "thinking": 32, "messages": [
                      { "kind": "text", "text": "hi" },
                      { "kind": "text_len", "wordCount": 5 },
                      { "kind": "tool_call", "name": "echo", "args": { "msg": "hey" } }
                  ]}
                ]
              }]
            }
            """;

        var responder = JsonScenarioLoader.Parse(json);

        responder.RemainingTurns["demo"].Should().Be(1);
    }

    [Fact]
    public void Parse_throws_on_empty_roles()
    {
        Action act = () => JsonScenarioLoader.Parse("""{ "roles": [] }""");

        act.Should().Throw<JsonScenarioFormatException>().WithMessage("*at least one role*");
    }

    [Fact]
    public void Parse_throws_on_missing_role_key()
    {
        const string json = """
            { "roles": [{ "match": { "type": "always" }, "turns": [
                { "messages": [{ "kind": "text", "text": "x" }] }
            ]}]}
            """;

        Action act = () => JsonScenarioLoader.Parse(json);

        act.Should().Throw<JsonScenarioFormatException>().WithMessage("*non-empty 'key'*");
    }

    [Fact]
    public void Parse_throws_on_unknown_match_type()
    {
        const string json = """
            { "roles": [{ "key": "demo", "match": { "type": "weather" }, "turns": [
                { "messages": [{ "kind": "text", "text": "x" }] }
            ]}]}
            """;

        Action act = () => JsonScenarioLoader.Parse(json);

        act.Should().Throw<JsonScenarioFormatException>().WithMessage("*weather*");
    }

    [Fact]
    public void Parse_throws_when_system_contains_omits_value()
    {
        const string json = """
            { "roles": [{ "key": "demo", "match": { "type": "system_contains" }, "turns": [
                { "messages": [{ "kind": "text", "text": "x" }] }
            ]}]}
            """;

        Action act = () => JsonScenarioLoader.Parse(json);

        act.Should().Throw<JsonScenarioFormatException>().WithMessage("*system_contains*'value'*");
    }

    [Fact]
    public void Parse_throws_when_user_contains_omits_value()
    {
        const string json = """
            { "roles": [{ "key": "demo", "match": { "type": "user_contains" }, "turns": [
                { "messages": [{ "kind": "text", "text": "x" }] }
            ]}]}
            """;

        Action act = () => JsonScenarioLoader.Parse(json);

        act.Should().Throw<JsonScenarioFormatException>().WithMessage("*user_contains*'value'*");
    }

    [Fact]
    public void Parse_throws_when_tool_match_omits_name()
    {
        const string json = """
            { "roles": [{ "key": "demo", "match": { "type": "tool" }, "turns": [
                { "messages": [{ "kind": "text", "text": "x" }] }
            ]}]}
            """;

        Action act = () => JsonScenarioLoader.Parse(json);

        act.Should().Throw<JsonScenarioFormatException>().WithMessage("*tool match*'name'*");
    }

    [Fact]
    public void Parse_throws_on_text_message_without_text()
    {
        const string json = """
            { "roles": [{ "key": "demo", "match": { "type": "always" }, "turns": [
                { "messages": [{ "kind": "text" }] }
            ]}]}
            """;

        Action act = () => JsonScenarioLoader.Parse(json);

        act.Should().Throw<JsonScenarioFormatException>().WithMessage("*Text message*'text'*");
    }

    [Fact]
    public void Parse_throws_on_text_len_without_word_count()
    {
        const string json = """
            { "roles": [{ "key": "demo", "match": { "type": "always" }, "turns": [
                { "messages": [{ "kind": "text_len" }] }
            ]}]}
            """;

        Action act = () => JsonScenarioLoader.Parse(json);

        act.Should().Throw<JsonScenarioFormatException>().WithMessage("*text_len*wordCount*");
    }

    [Fact]
    public void Parse_throws_on_tool_call_without_name()
    {
        const string json = """
            { "roles": [{ "key": "demo", "match": { "type": "always" }, "turns": [
                { "messages": [{ "kind": "tool_call" }] }
            ]}]}
            """;

        Action act = () => JsonScenarioLoader.Parse(json);

        act.Should().Throw<JsonScenarioFormatException>().WithMessage("*tool_call*'name'*");
    }

    [Fact]
    public void Parse_throws_on_unknown_message_kind()
    {
        const string json = """
            { "roles": [{ "key": "demo", "match": { "type": "always" }, "turns": [
                { "messages": [{ "kind": "image", "text": "ignored" }] }
            ]}]}
            """;

        Action act = () => JsonScenarioLoader.Parse(json);

        act.Should().Throw<JsonScenarioFormatException>().WithMessage("*image*");
    }

    [Fact]
    public void Parse_throws_on_malformed_json()
    {
        Action act = () => JsonScenarioLoader.Parse("not json");

        act.Should().Throw<JsonScenarioFormatException>().WithMessage("*malformed*");
    }

    [Fact]
    public void Load_reads_an_absolute_file_path()
    {
        var temp = Path.Combine(Path.GetTempPath(), $"scenario-{Guid.NewGuid():N}.json");
        File.WriteAllText(
            temp,
            """
            { "roles": [{ "key": "from-disk", "match": { "type": "always" }, "turns": [
                { "messages": [{ "kind": "text", "text": "from disk" }] }
            ]}]}
            """
        );

        try
        {
            var responder = JsonScenarioLoader.Load(temp);

            responder.RemainingTurns.Should().ContainKey("from-disk");
        }
        finally
        {
            File.Delete(temp);
        }
    }

    [Fact]
    public void Load_throws_FileNotFound_for_unresolved_name()
    {
        Action act = () => JsonScenarioLoader.Load("does-not-exist-anywhere");

        act.Should().Throw<FileNotFoundException>();
    }

    [Fact]
    public void Load_rejects_blank_input()
    {
        Action act = () => JsonScenarioLoader.Load("   ");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task Parse_user_contains_matcher_dispatches_only_on_matching_request()
    {
        // Closes the JSON → matcher → dispatch gap: ensures the parsed user_contains
        // delegate actually claims a request whose user message contains the value, and
        // declines to claim one that does not.
        const string json = """
            {
              "roles": [
                { "key": "greeter", "match": { "type": "user_contains", "value": "hello" }, "turns": [
                    { "messages": [{ "kind": "text", "text": "greeting reply" }] }
                ]},
                { "key": "fallback", "match": { "type": "always" }, "turns": [
                    { "messages": [{ "kind": "text", "text": "fallback reply" }] },
                    { "messages": [{ "kind": "text", "text": "fallback reply 2" }] }
                ]}
              ]
            }
            """;

        var responder = JsonScenarioLoader.Parse(json);
        await using var fixture = await EphemeralHostFixture.StartAsync(responder);
        using var httpClient = new HttpClient();
        var openClient = new OpenClient(httpClient, fixture.BaseUrl + "/v1");

        await ConsumeAsync(openClient, "Hello there friend");
        await ConsumeAsync(openClient, "totally unrelated text");

        // Matching request consumed greeter's only turn; non-matching fell through to fallback.
        responder.RemainingTurns["greeter"].Should().Be(0);
        responder.RemainingTurns["fallback"].Should().Be(1);
    }

    [Fact]
    public async Task Parse_tool_result_matcher_dispatches_on_anthropic_tool_result_request()
    {
        const string json = """
            {
              "roles": [
                { "key": "after-tool", "match": { "type": "tool_result" }, "turns": [
                    { "messages": [{ "kind": "text", "text": "after tool result" }] }
                ]},
                { "key": "fallback", "match": { "type": "always" }, "turns": [
                    { "messages": [{ "kind": "text", "text": "fallback reply" }] }
                ]}
              ]
            }
            """;

        var responder = JsonScenarioLoader.Parse(json);
        await using var fixture = await EphemeralHostFixture.StartAsync(responder);
        using var httpClient = new HttpClient();
        var request = new StringContent(
            """
            {
              "model": "claude-test",
              "max_tokens": 1024,
              "stream": true,
              "messages": [
                {
                  "role": "user",
                  "content": [
                    {
                      "type": "tool_result",
                      "tool_use_id": "toolu_123",
                      "content": "fixture output"
                    }
                  ]
                }
              ]
            }
            """,
            Encoding.UTF8,
            "application/json"
        );

        using var response = await httpClient.PostAsync($"{fixture.BaseUrl}/v1/messages", request);
        var sse = await response.Content.ReadAsStringAsync();

        response.EnsureSuccessStatusCode();
        sse.Should().Contain("after tool result");
        responder.RemainingTurns["after-tool"].Should().Be(0);
        responder.RemainingTurns["fallback"].Should().Be(1);
    }

    [Fact]
    public async Task Parse_tool_result_matcher_dispatches_on_openai_responses_function_call_output_request()
    {
        const string json = """
            {
              "roles": [
                { "key": "after-tool", "match": { "type": "tool_result" }, "turns": [
                    { "messages": [{ "kind": "text", "text": "after responses tool output" }] }
                ]},
                { "key": "fallback", "match": { "type": "always" }, "turns": [
                    { "messages": [{ "kind": "text", "text": "fallback reply" }] }
                ]}
              ]
            }
            """;

        var responder = JsonScenarioLoader.Parse(json);
        await using var fixture = await EphemeralHostFixture.StartAsync(responder);
        using var httpClient = new HttpClient();
        var request = new StringContent(
            """
            {
              "model": "gpt-test",
              "stream": true,
              "input": [
                {
                  "type": "function_call_output",
                  "call_id": "call_123",
                  "output": "fixture output"
                }
              ]
            }
            """,
            Encoding.UTF8,
            "application/json"
        );

        using var response = await httpClient.PostAsync($"{fixture.BaseUrl}/v1/responses", request);
        var sse = await response.Content.ReadAsStringAsync();

        response.EnsureSuccessStatusCode();
        sse.Should().Contain("after responses tool output");
        responder.RemainingTurns["after-tool"].Should().Be(0);
        responder.RemainingTurns["fallback"].Should().Be(1);
    }

    private static async Task ConsumeAsync(OpenClient openClient, string userMessage)
    {
        var request = new ChatCompletionRequest(
            "gpt-test",
            [new ChatMessage { Role = RoleEnum.User, Content = ChatMessage.CreateContent(userMessage) }]
        )
        {
            Stream = true,
        };
        await foreach (var _ in openClient.StreamingChatCompletionsAsync(request)) { }
    }
}
