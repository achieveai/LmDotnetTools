namespace AchieveAi.LmDotnetTools.AnthropicProvider.Tests.Models;

/// <summary>
///     A tool's input arrives as many input_json_delta chunks. The parser must not parse the
///     partial input on every chunk: each parse of an unfinished prefix throws, and re-parsing the
///     whole buffer per chunk is quadratic (issue #928).
/// </summary>
public class ToolInputStreamParserTests
{
    private static readonly string FullInput = JsonSerializer.Serialize(
        new { path = "src/app.cs", content = string.Concat(Enumerable.Repeat("line of file content\n", 40)) }
    );

    [Fact]
    public void ProcessEvent_ToolUse_ChunkedInput_ParsesWithoutThrowing()
    {
        var parser = new AnthropicStreamParser();
        parser.ProcessEvent("event", BuildMessageStart());
        parser.ProcessEvent("event", BuildBlockStart("tool_use", "toolu_01", "write_file"));
        var chunks = Chunk(FullInput, 7);

        var (updates, throws) = CountJsonExceptions(() =>
            chunks.SelectMany(c => parser.ProcessEvent("event", BuildInputDelta(c))).ToList()
        );
        var (stop, stopThrows) = CountJsonExceptions(() =>
            parser.ProcessEvent("event", JsonSerializer.Serialize(new { type = "content_block_stop", index = 0 }))
        );

        Assert.True(chunks.Count > 50);
        Assert.Equal(0, throws);
        Assert.Equal(0, stopThrows);
        // Each chunk still streams out as it arrives.
        Assert.Equal(chunks.Count, updates.Count);
        Assert.Equal(
            FullInput,
            string.Concat(updates.Cast<ToolsCallUpdateMessage>().Select(u => u.ToolCallUpdates[0].FunctionArgs))
        );
        var call = Assert.IsType<ToolsCallMessage>(Assert.Single(stop)).ToolCalls[0];
        Assert.Equal("toolu_01", call.ToolCallId);
        Assert.Equal(FullInput, call.FunctionArgs);
    }

    [Fact]
    public void ProcessEvent_ServerToolUse_ChunkedInput_ParsesWithoutThrowing()
    {
        var parser = new AnthropicStreamParser();
        parser.ProcessEvent("event", BuildMessageStart());
        parser.ProcessEvent("event", BuildBlockStart("server_tool_use", "srvtoolu_01", "web_search"));
        var chunks = Chunk(FullInput, 7);

        var (updates, throws) = CountJsonExceptions(() =>
            chunks.SelectMany(c => parser.ProcessEvent("event", BuildInputDelta(c))).ToList()
        );
        var stop = parser.ProcessEvent(
            "event",
            JsonSerializer.Serialize(new { type = "content_block_stop", index = 0 })
        );

        Assert.Equal(0, throws);
        Assert.Empty(updates);
        var final = Assert.IsType<ToolCallUpdateMessage>(Assert.Single(stop));
        Assert.Equal(
            JsonSerializer.Deserialize<JsonElement>(FullInput).GetProperty("content").GetString(),
            JsonSerializer.Deserialize<JsonElement>(final.FunctionArgs!).GetProperty("content").GetString()
        );
    }

    [Fact]
    public void ProcessEvent_ToolUse_InputCutOff_FallsBackToStartInput()
    {
        var parser = new AnthropicStreamParser();
        parser.ProcessEvent("event", BuildMessageStart());
        parser.ProcessEvent("event", BuildBlockStart("tool_use", "toolu_02", "write_file"));
        foreach (var chunk in Chunk(FullInput[..(FullInput.Length / 2)], 7))
        {
            parser.ProcessEvent("event", BuildInputDelta(chunk));
        }

        var stop = parser.ProcessEvent(
            "event",
            JsonSerializer.Serialize(new { type = "content_block_stop", index = 0 })
        );

        var call = Assert.IsType<ToolsCallMessage>(Assert.Single(stop)).ToolCalls[0];
        Assert.Equal("{}", call.FunctionArgs);
    }

    /// <summary>Counts JsonExceptions thrown, caught or not, while <paramref name="action"/> runs on this flow.</summary>
    private static (T Result, int Throws) CountJsonExceptions<T>(Func<T> action)
    {
        var counting = new AsyncLocal<bool>();
        var throws = 0;
        void OnFirstChance(object? sender, System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs e)
        {
            if (counting.Value && e.Exception is JsonException)
            {
                _ = Interlocked.Increment(ref throws);
            }
        }

        AppDomain.CurrentDomain.FirstChanceException += OnFirstChance;
        try
        {
            counting.Value = true;
            var result = action();
            counting.Value = false;
            return (result, throws);
        }
        finally
        {
            AppDomain.CurrentDomain.FirstChanceException -= OnFirstChance;
        }
    }

    private static List<string> Chunk(string text, int size)
    {
        return
        [
            .. Enumerable
                .Range(0, (text.Length + size - 1) / size)
                .Select(i => text.Substring(i * size, Math.Min(size, text.Length - (i * size)))),
        ];
    }

    private static string BuildInputDelta(string partialJson)
    {
        return JsonSerializer.Serialize(
            new
            {
                type = "content_block_delta",
                index = 0,
                delta = new { type = "input_json_delta", partial_json = partialJson },
            }
        );
    }

    private static string BuildBlockStart(string type, string id, string name)
    {
        return JsonSerializer.Serialize(
            new
            {
                type = "content_block_start",
                index = 0,
                content_block = new
                {
                    type,
                    id,
                    name,
                    input = new { },
                },
            }
        );
    }

    private static string BuildMessageStart()
    {
        return JsonSerializer.Serialize(
            new
            {
                type = "message_start",
                message = new
                {
                    id = "msg_stream_01",
                    type = "message",
                    role = "assistant",
                    model = "claude-sonnet-4-20250514",
                    content = Array.Empty<object>(),
                    usage = new { input_tokens = 100, output_tokens = 0 },
                },
            }
        );
    }
}
