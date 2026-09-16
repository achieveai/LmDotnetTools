using AchieveAi.LmDotnetTools.LmMultiTurn.Lifecycle;
using LmStreaming.Sample.Services;

namespace LmStreaming.Sample.Tests.Services;

/// <summary>
/// The experimental elapsed-time notice is model-only context. <see cref="TranscriptProjection"/> is the
/// one seam every transcript reader goes through (cross-agent endpoint, <c>GetAgentTranscript</c>, and
/// the workspace mirror), so the notice is dropped there regardless of the reasoning switch.
/// </summary>
public class TranscriptProjectionElapsedTimeNoticeTests
{
    private const string ThreadId = "thread-projection";

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Normalize_DropsTheElapsedTimeNotice_ForEveryReader(bool excludeReasoning)
    {
        var notice = ElapsedTimeNotice.Build(
            TimeSpan.FromSeconds(61),
            new DateTimeOffset(2026, 9, 15, 14, 3, 20, TimeSpan.Zero)
        );
        PersistedMessage[] rows =
        [
            MessagePersistenceConverter.ToPersistedMessage(
                new TextMessage { Text = "hello", Role = Role.User },
                ThreadId,
                "run-1"
            ),
            MessagePersistenceConverter.ToPersistedMessage(notice, ThreadId, "run-1"),
            MessagePersistenceConverter.ToPersistedMessage(
                new TextMessage { Text = "done", Role = Role.Assistant },
                ThreadId,
                "run-1"
            ),
        ];

        var projected = TranscriptProjection.Normalize(rows, excludeReasoning);

        projected
            .Select(r => JsonDocument.Parse(r.MessageJson).RootElement.GetProperty("text").GetString())
            .Should()
            .Equal("hello", "done");
        projected.Should().OnlyContain(r => !r.MessageJson.Contains(ElapsedTimeNotice.MetadataKey));

        // The input is untouched: the store row is the model's, and Normalize only shapes a copy.
        rows.Should().HaveCount(3);
    }
}
