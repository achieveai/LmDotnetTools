using System.Text.Json;
using System.Text.Json.Serialization;

namespace TodoEval.Runner.Metrics;

/// <summary>
/// Round-trips <see cref="CountMap"/> as a plain JSON object.
/// </summary>
/// <remarks>
/// Writing needs no converter — <c>CountMap</c> is an <c>IReadOnlyDictionary</c> and the serializer
/// already emits the right shape — but READING one back does: the type has no public constructor, so
/// an archived <c>runs.jsonl</c> could not be deserialized at all without this. #677 has to read the
/// baseline's rows back off disk, and a comparison that silently dropped every error-code tally would
/// publish "no residual errors" for a sweep that was full of them.
/// <para>
/// The write side is kept here and delegates to the same enumeration the default serializer used, so
/// registering the converter does not change one byte of an already-committed archive.
/// </para>
/// </remarks>
internal sealed class CountMapJsonConverter : JsonConverter<CountMap>
{
    public override CountMap Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var counts = JsonSerializer.Deserialize<Dictionary<string, int>>(ref reader, options);
        return counts is null ? CountMap.Empty : CountMap.From(counts);
    }

    public override void Write(Utf8JsonWriter writer, CountMap value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        foreach (var (key, count) in value)
        {
            writer.WriteNumber(key, count);
        }

        writer.WriteEndObject();
    }
}
