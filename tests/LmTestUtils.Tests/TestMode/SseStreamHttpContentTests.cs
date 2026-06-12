using System.Text;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmTestUtils.TestMode;

namespace AchieveAi.LmDotnetTools.LmTestUtils.Tests.TestMode;

public class SseStreamHttpContentTests
{
    [Fact]
    public async Task OpenAiStream_ExplicitText_ReassemblesWithSpacesAcrossChunkBoundaries()
    {
        // Six words force two 3-word chunks; the space between word 3 and word 4 lands on a
        // chunk boundary, which previously vanished when the client concatenated the deltas.
        const string OriginalText = "alpha beta gamma delta epsilon zeta";

        var plan = new InstructionPlan(
            "chunk-space-test",
            reasoningLength: null,
            messages: [InstructionMessage.ForExplicitText(OriginalText)]
        );

        var assembled = await StreamAndReassembleContentAsync(plan, wordsPerChunk: 3);

        Assert.Equal(OriginalText, assembled);
    }

    [Fact]
    public async Task OpenAiStream_ExplicitText_PreservesInteriorSpacingInJsonPayload()
    {
        // Mirrors the request_params_echo:tools use case: a compact JSON blob streamed through
        // the word-chunker must survive reassembly byte-for-byte.
        const string OriginalText =
            "{\"tools\":[{\"name\":\"sandbox-Read\",\"description\":\"Reads a file from the filesystem.\"}]}";

        var plan = new InstructionPlan(
            "chunk-space-json-test",
            reasoningLength: null,
            messages: [InstructionMessage.ForExplicitText(OriginalText)]
        );

        var assembled = await StreamAndReassembleContentAsync(plan, wordsPerChunk: 3);

        Assert.Equal(OriginalText, assembled);
    }

    private static async Task<string> StreamAndReassembleContentAsync(InstructionPlan plan, int wordsPerChunk)
    {
        var content = new SseStreamHttpContent(
            plan,
            model: "test-model",
            wordsPerChunk: wordsPerChunk,
            chunkDelayMs: 0
        );

        using var stream = new MemoryStream();
        await content.CopyToAsync(stream);
        var sse = Encoding.UTF8.GetString(stream.ToArray());

        var builder = new StringBuilder();
        foreach (var line in sse.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');
            if (!trimmed.StartsWith("data: ", StringComparison.Ordinal))
            {
                continue;
            }

            var payload = trimmed["data: ".Length..];
            if (payload == "[DONE]")
            {
                break;
            }

            using var doc = JsonDocument.Parse(payload);
            if (!doc.RootElement.TryGetProperty("choices", out var choices)
                || choices.ValueKind != JsonValueKind.Array
                || choices.GetArrayLength() == 0)
            {
                continue;
            }

            var delta = choices[0].GetProperty("delta");
            if (delta.TryGetProperty("content", out var contentEl)
                && contentEl.ValueKind == JsonValueKind.String)
            {
                builder.Append(contentEl.GetString());
            }
        }

        return builder.ToString();
    }
}
