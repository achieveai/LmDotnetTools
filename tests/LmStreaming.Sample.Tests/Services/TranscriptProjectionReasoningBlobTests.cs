using LmStreaming.Sample.Services;

namespace LmStreaming.Sample.Tests.Services;

/// <summary>
/// A replayable reasoning blob is for the model that made it. <see cref="TranscriptProjection"/> is the seam every
/// transcript reader goes through, so the blob is dropped there whatever the reasoning switch says.
/// </summary>
public class TranscriptProjectionReasoningBlobTests
{
    private const string ThreadId = "thread-blob";

    [Fact]
    public void Normalize_drops_tagged_reasoning_row()
    {
        PersistedMessage[] rows =
        [
            MessagePersistenceConverter.ToPersistedMessage(
                new TextMessage { Text = "hello", Role = Role.User },
                ThreadId,
                "run-1"
            ),
            MessagePersistenceConverter.ToPersistedMessage(
                new ReasoningMessage
                {
                    Reasoning = "BLOB",
                    Visibility = ReasoningVisibility.Encrypted,
                    Role = Role.Assistant,
                    EncryptedOrigin = "responses:m",
                },
                ThreadId,
                "run-1"
            ),
            MessagePersistenceConverter.ToPersistedMessage(
                new ReasoningMessage
                {
                    Reasoning = "sig",
                    Visibility = ReasoningVisibility.Encrypted,
                    Role = Role.Assistant,
                },
                ThreadId,
                "run-1"
            ),
        ];

        var projected = TranscriptProjection.Normalize(rows, excludeReasoning: false);

        projected.Should().HaveCount(2, "only the tagged row is hidden; an untagged row keeps today's path");
        projected.Should().OnlyContain(r => !r.MessageJson.Contains("BLOB"));
        rows.Should().HaveCount(3);
    }
}
