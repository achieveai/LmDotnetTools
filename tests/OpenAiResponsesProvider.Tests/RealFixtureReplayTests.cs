using System.Text.Json;
using AchieveAi.LmDotnetTools.OpenAiResponsesProvider.Models;
using FluentAssertions;

namespace AchieveAi.LmDotnetTools.OpenAiResponsesProvider.Tests;

/// <summary>
///     Replay-based validation against real (redacted) WebSocket capture fixtures from
///     <c>samples/MockProviderHost/fixtures/openai-responses-websocket/</c>. Each fixture is a
///     verbatim transcript of Codex talking to a live OpenAI Responses API server, so it
///     exercises every event shape, ordering, and edge-case the wire actually produces — far
///     beyond what hand-written test JSON can cover. If OpenAI introduces a new event type or
///     subtly changes a payload shape, a refreshed fixture surfaces the divergence here.
/// </summary>
public sealed class RealFixtureReplayTests
{
    public static IEnumerable<object[]> FixtureFiles()
    {
        var dir = LocateFixtureDirectory();
        foreach (var path in Directory.EnumerateFiles(dir, "sample_ws_stream_*.redacted.json"))
        {
            yield return new object[] { Path.GetFileName(path) };
        }
    }

    [Theory]
    [MemberData(nameof(FixtureFiles))]
    public void Every_server_event_round_trips_through_parser(string fixtureName)
    {
        var frames = LoadFrames(fixtureName);
        var serverFrames = frames.Where(f => !f.FromClient).ToList();
        serverFrames.Should().NotBeEmpty("fixture must contain server-emitted events");

        foreach (var frame in serverFrames)
        {
            var ev = ResponseEventParser.Parse(frame.Text);
            ev.Type.Should().NotBeNullOrEmpty();

            // Verify the typed record carries the discriminator the wire shipped (catches a
            // GenericResponseEvent fallback for any newly-emitted event subtype).
            var rendered = ResponseEventParser.ToJsonObject(ev);
            rendered["type"]!.GetValue<string>().Should().Be(ev.Type);
        }
    }

    [Theory]
    [MemberData(nameof(FixtureFiles))]
    public void All_known_lifecycle_events_decode_to_typed_records(string fixtureName)
    {
        var frames = LoadFrames(fixtureName);
        var byType = frames
            .Where(f => !f.FromClient)
            .Select(f => ResponseEventParser.Parse(f.Text))
            .GroupBy(e => e.Type)
            .ToDictionary(g => g.Key, g => g.First());

        // Every fixture exercises the full request lifecycle.
        byType.Should().ContainKey(ResponseEventTypes.ResponseCreated);
        byType.Should().ContainKey(ResponseEventTypes.ResponseInProgress);
        byType.Should().ContainKey(ResponseEventTypes.ResponseCompleted);

        byType[ResponseEventTypes.ResponseCreated].Should().BeOfType<ResponseLifecycleEvent>();
        byType[ResponseEventTypes.ResponseInProgress].Should().BeOfType<ResponseLifecycleEvent>();
        byType[ResponseEventTypes.ResponseCompleted].Should().BeOfType<ResponseLifecycleEvent>();
    }

    [Theory]
    [MemberData(nameof(FixtureFiles))]
    public void Function_call_arguments_streaming_produces_well_formed_json_after_assembly(
        string fixtureName
    )
    {
        var frames = LoadFrames(fixtureName);
        var perItem = new Dictionary<string, System.Text.StringBuilder>(StringComparer.Ordinal);
        var doneItems = new HashSet<string>(StringComparer.Ordinal);

        foreach (var frame in frames.Where(f => !f.FromClient))
        {
            var ev = ResponseEventParser.Parse(frame.Text);
#pragma warning disable IDE0010 // Add missing cases - intentional: only function-call branches matter here
            switch (ev)
            {
                case ResponseFunctionCallArgumentsDeltaEvent delta:
                    if (!perItem.TryGetValue(delta.ItemId, out var buf))
                    {
                        buf = new System.Text.StringBuilder();
                        perItem[delta.ItemId] = buf;
                    }

                    _ = buf.Append(delta.Delta);
                    break;
                case ResponseFunctionCallArgumentsDoneEvent done:
                    _ = doneItems.Add(done.ItemId);
                    // The 'arguments' field on the done event must equal the assembled deltas.
                    if (perItem.TryGetValue(done.ItemId, out var assembled))
                    {
                        assembled.ToString().Should().Be(done.Arguments,
                            $"streamed deltas must concatenate to the final arguments for item {done.ItemId}");
                    }

                    // The final arguments must be valid JSON the agent can hand to the tool.
                    using (var doc = JsonDocument.Parse(done.Arguments))
                    {
                        doc.RootElement.ValueKind.Should().Be(JsonValueKind.Object);
                    }

                    break;
            }
#pragma warning restore IDE0010
        }

        doneItems.Should().NotBeEmpty("fixture exercises tool-calling, expected ≥1 function_call_arguments.done");
    }

    [Theory]
    [MemberData(nameof(FixtureFiles))]
    public void Client_response_create_payloads_have_documented_shape(string fixtureName)
    {
        var frames = LoadFrames(fixtureName);
        var clientFrames = frames.Where(f => f.FromClient).ToList();
        clientFrames.Should().NotBeEmpty();

        foreach (var frame in clientFrames)
        {
            using var doc = JsonDocument.Parse(frame.Text);
            var root = doc.RootElement;
            root.GetProperty("type").GetString().Should().Be("response.create");
            root.GetProperty("input").ValueKind.Should().Be(JsonValueKind.Array);
            root.GetProperty("stream").GetBoolean().Should().BeTrue();
        }
    }

    private static readonly JsonSerializerOptions s_fixtureOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static List<CapturedFrame> LoadFrames(string fixtureName)
    {
        var path = Path.Combine(LocateFixtureDirectory(), fixtureName);
        using var stream = File.OpenRead(path);
        var raw = JsonSerializer.Deserialize<List<RawFrame>>(stream, s_fixtureOptions)
            ?? throw new InvalidOperationException($"Fixture {fixtureName} did not deserialize");
        return raw.ConvertAll(r => new CapturedFrame(r.Text ?? string.Empty, r.From_Client));
    }

    private static string LocateFixtureDirectory()
    {
        // Walk up from the test assembly directory until we find the repo's samples/ folder.
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 10 && dir is not null; i++)
        {
            var candidate = Path.Combine(
                dir,
                "samples",
                "MockProviderHost",
                "fixtures",
                "openai-responses-websocket"
            );
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            dir = Path.GetDirectoryName(dir);
        }

        throw new DirectoryNotFoundException(
            "Could not locate samples/MockProviderHost/fixtures/openai-responses-websocket/ "
            + "from " + AppContext.BaseDirectory
        );
    }

    private sealed record CapturedFrame(string Text, bool FromClient);

    private sealed record RawFrame(string? Text, bool From_Client);
}
