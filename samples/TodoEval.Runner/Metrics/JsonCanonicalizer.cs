using System.Text.Json;

namespace TodoEval.Runner.Metrics;

/// <summary>
/// Canonicalizes a tool call's <c>function_args</c> JSON string per metrics-spec.md — object keys
/// ordinal-sorted at every level, compact output. Absent/empty args canonicalize to <c>""</c>. A
/// PARSE failure falls back to the raw string exactly as recorded (not trimmed — the spec's oracle
/// keeps it verbatim): a malformed args payload is itself a model failure the metrics must still
/// count, and it must compare equal only to byte-identical retries.
/// </summary>
internal static class JsonCanonicalizer
{
    public static string CanonicalizeArgs(string? rawArgs)
    {
        if (string.IsNullOrEmpty(rawArgs))
        {
            return "";
        }

        try
        {
            using var doc = JsonDocument.Parse(rawArgs);
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                WriteCanonical(doc.RootElement, writer);
            }

            return System.Text.Encoding.UTF8.GetString(stream.ToArray());
        }
        catch (JsonException)
        {
            return rawArgs;
        }
    }

    private static void WriteCanonical(JsonElement element, Utf8JsonWriter writer)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var property in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
            {
                writer.WritePropertyName(property.Name);
                WriteCanonical(property.Value, writer);
            }

            writer.WriteEndObject();
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray();
            foreach (var item in element.EnumerateArray())
            {
                WriteCanonical(item, writer);
            }

            writer.WriteEndArray();
        }
        else
        {
            element.WriteTo(writer);
        }
    }
}
