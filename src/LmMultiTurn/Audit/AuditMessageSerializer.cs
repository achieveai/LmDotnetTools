using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Utils;

namespace AchieveAi.LmDotnetTools.LmMultiTurn.Audit;

/// <summary>Produces stable UTF-8 source bytes and deterministic audit identifiers.</summary>
public static class AuditMessageSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false,
        Converters = { new IMessageJsonConverter() },
    };

    public static byte[] SerializeRequest(IReadOnlyList<IMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("messages");
            writer.WriteStartArray();
            foreach (var message in messages)
            {
                ArgumentNullException.ThrowIfNull(message);
                writer.WriteStartObject();
                writer.WriteString("runtime_type", message.GetType().FullName ?? message.GetType().Name);
                writer.WritePropertyName("message");
                using var document = JsonDocument.Parse(
                    JsonSerializer.SerializeToUtf8Bytes(message, typeof(IMessage), Options)
                );
                WriteCanonicalValue(writer, document.RootElement);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    public static byte[] SerializeMessage(IMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        using var document = JsonDocument.Parse(
            JsonSerializer.SerializeToUtf8Bytes(message, typeof(IMessage), Options)
        );
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteCanonicalValue(writer, document.RootElement);
        }

        return stream.ToArray();
    }

    private static void WriteCanonicalValue(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (
                    var property in value
                        .EnumerateObject()
                        .OrderBy(property => property.Name is "$type" or "type" ? 0 : 1)
                        .ThenBy(property => property.Name, StringComparer.Ordinal)
                )
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonicalValue(writer, property.Value);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray())
                {
                    WriteCanonicalValue(writer, item);
                }

                writer.WriteEndArray();
                break;
            case JsonValueKind.Undefined:
            case JsonValueKind.String:
            case JsonValueKind.Number:
            case JsonValueKind.True:
            case JsonValueKind.False:
            case JsonValueKind.Null:
                value.WriteTo(writer);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(value), value.ValueKind, "Unsupported JSON value kind.");
        }
    }

    public static string ComputeSha256(ReadOnlySpan<byte> content) =>
        Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    public static string BuildParentTurnId(
        string threadId,
        string runId,
        string generationId,
        string? spawningToolCallId
    )
    {
        var identity = string.Join("\n", threadId, runId, generationId, spawningToolCallId ?? string.Empty);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
    }

    public static string BuildRecordId(
        MultiTurnAuditScope scope,
        string threadId,
        string runId,
        string generationId,
        long sequence,
        string recordType,
        string contentSha256
    )
    {
        ArgumentNullException.ThrowIfNull(scope);
        var identity = string.Join(
            "\n",
            scope.EngagementId,
            scope.RoundId,
            threadId,
            runId,
            generationId,
            sequence.ToString(System.Globalization.CultureInfo.InvariantCulture),
            recordType,
            contentSha256
        );
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
    }
}
