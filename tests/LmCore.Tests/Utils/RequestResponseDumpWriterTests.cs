using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;

namespace AchieveAi.LmDotnetTools.LmCore.Tests.Utils;

public class RequestResponseDumpWriterTests
{
    [Fact]
    public void WriteRequest_WhenIoFails_DoesNotThrow()
    {
        var blockedPath = Path.Combine(Path.GetTempPath(), $"dump-blocked-{Guid.NewGuid():N}");
        File.WriteAllText(blockedPath, "blocking-file");

        try
        {
            var baseFileName = Path.Combine(blockedPath, "request-dump");
            var writer = AchieveAi.LmDotnetTools.LmCore.Utils.RequestResponseDumpWriter.Create(
                baseFileName,
                new JsonSerializerOptions(),
                NullLogger.Instance
            );

            Assert.NotNull(writer);
            var ex = Record.Exception(() => writer.WriteRequest(new { Value = "test" }));
            Assert.Null(ex);
        }
        finally
        {
            if (File.Exists(blockedPath))
            {
                File.Delete(blockedPath);
            }
        }
    }

    [Fact]
    public void WriteRequest_WhenSerializationFails_Throws()
    {
        var baseFileName = Path.Combine(Path.GetTempPath(), $"dump-serialization-{Guid.NewGuid():N}");
        var writer = AchieveAi.LmDotnetTools.LmCore.Utils.RequestResponseDumpWriter.Create(
            baseFileName,
            new JsonSerializerOptions(),
            NullLogger.Instance
        );

        Assert.NotNull(writer);
        Assert.Throws<NotSupportedException>(() => writer.WriteRequest(new NonSerializablePayload()));
    }

    [Fact]
    public void AppendResponseChunk_WhenIoFails_DoesNotThrowEvenOnRepeatedCalls()
    {
        var blockedPath = Path.Combine(Path.GetTempPath(), $"dump-stream-blocked-{Guid.NewGuid():N}");
        File.WriteAllText(blockedPath, "blocking-file");

        try
        {
            var baseFileName = Path.Combine(blockedPath, "stream-dump");
            var writer = AchieveAi.LmDotnetTools.LmCore.Utils.RequestResponseDumpWriter.Create(
                baseFileName,
                new JsonSerializerOptions(),
                NullLogger.Instance
            );

            Assert.NotNull(writer);
            var ex1 = Record.Exception(() => writer.AppendResponseChunk(new { Chunk = 1 }));
            var ex2 = Record.Exception(() => writer.AppendResponseChunk(new { Chunk = 2 }));
            Assert.Null(ex1);
            Assert.Null(ex2);
        }
        finally
        {
            if (File.Exists(blockedPath))
            {
                File.Delete(blockedPath);
            }
        }
    }

    [Fact]
    public void AppendRawResponseLine_WritesTheTextVerbatim_OnePerLine()
    {
        var baseFileName = Path.Combine(Path.GetTempPath(), $"dump-raw-{Guid.NewGuid():N}");
        var writer = AchieveAi.LmDotnetTools.LmCore.Utils.RequestResponseDumpWriter.Create(
            baseFileName,
            new JsonSerializerOptions(),
            NullLogger.Instance
        );

        try
        {
            Assert.NotNull(writer);
            // Not serialized: a raw wire payload must land as-is, quotes and spacing untouched.
            writer.AppendRawResponseLine("""{"type":"a",  "delta":" fee"}""");
            writer.AppendRawResponseLine("""{"type":"b"}""");

            var lines = File.ReadAllLines(baseFileName + ".response.txt");
            Assert.Equal(["""{"type":"a",  "delta":" fee"}""", """{"type":"b"}"""], lines);
        }
        finally
        {
            File.Delete(baseFileName + ".response.txt");
        }
    }

    private sealed class NonSerializablePayload
    {
        public Action Callback { get; } = static () => { };
    }
}
