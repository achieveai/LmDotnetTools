using System.Text.Json;
using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Models.JsonlEvents;

/// <summary>
///     Tolerant per-item converter for <see cref="NamedRef"/>.
///     <para>
///         The claude-agent-sdk CLI has shipped <c>system.init.plugins</c> as both
///         <c>string[]</c> and <c>[{name, path}]</c> (see #42 / PR #43). The sibling
///         <c>skills</c>/<c>agents</c> fields are at risk of the same drift.
///         This converter accepts either shape per-item so that a single drifted
///         element no longer drops the entire <c>system.init</c> frame.
///     </para>
///     <list type="bullet">
///         <item><see cref="JsonTokenType.String"/> → <c>new NamedRef { Name = value }</c>.</item>
///         <item><see cref="JsonTokenType.StartObject"/> → deserialize as <see cref="NamedRef"/>
///             (including <see cref="NamedRef.Extra"/> overflow).</item>
///         <item>Anything else, including a literal JSON <c>null</c> element, → throw
///             <see cref="JsonException"/>. <c>ParseLine</c> catches this and logs a
///             warning, surfacing drift outside the tolerated envelope rather than
///             silently passing an element into a non-nullable list slot.</item>
///     </list>
/// </summary>
public sealed class NamedRefJsonConverter : JsonConverter<NamedRef>
{
    // Force STJ to route literal JSON `null` array elements through Read() instead
    // of short-circuiting and inserting a raw null into List<NamedRef>; the Read()
    // null-token branch then throws JsonException, matching the rest of the
    // out-of-envelope handling.
    public override bool HandleNull => true;

    public override NamedRef Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.TokenType switch
        {
            JsonTokenType.String => new NamedRef { Name = reader.GetString() },
            JsonTokenType.StartObject => ReadObject(ref reader),
            // Explicitly reject null array elements: List<NamedRef> declares a
            // non-nullable element type, and silently letting null sneak into
            // the list would reintroduce the same NRE-shaped failure mode this
            // converter exists to prevent (consumers iterating `.Name` on null).
            _ => throw new JsonException(
                $"Unexpected token '{reader.TokenType}' when reading NamedRef; expected String or StartObject."),
        };
    }

    public override void Write(Utf8JsonWriter writer, NamedRef value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);

        writer.WriteStartObject();
        if (value.Name != null)
        {
            writer.WriteString("name", value.Name);
        }

        if (value.Path != null)
        {
            writer.WriteString("path", value.Path);
        }

        if (value.Extra != null)
        {
            foreach (var kvp in value.Extra)
            {
                writer.WritePropertyName(kvp.Key);
                kvp.Value.WriteTo(writer);
            }
        }

        writer.WriteEndObject();
    }

    private static NamedRef ReadObject(ref Utf8JsonReader reader)
    {
        string? name = null;
        string? path = null;
        Dictionary<string, JsonElement>? extra = null;

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                return new NamedRef
                {
                    Name = name,
                    Path = path,
                    Extra = extra,
                };
            }

            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new JsonException($"Unexpected token '{reader.TokenType}' inside NamedRef object.");
            }

            var propertyName = reader.GetString();
            if (!reader.Read())
            {
                throw new JsonException(
                    $"Unexpected end of JSON after property '{propertyName}' inside NamedRef object.");
            }

            switch (propertyName)
            {
                case "name":
                    name = ReadOptionalString(ref reader, propertyName);
                    break;
                case "path":
                    path = ReadOptionalString(ref reader, propertyName);
                    break;
                default:
                    extra ??= [];
                    using (var doc = JsonDocument.ParseValue(ref reader))
                    {
                        extra[propertyName!] = doc.RootElement.Clone();
                    }
                    break;
            }
        }

        throw new JsonException("Unexpected end of JSON while reading NamedRef.");
    }

    private static string? ReadOptionalString(ref Utf8JsonReader reader, string propertyName)
    {
        return reader.TokenType switch
        {
            JsonTokenType.Null => null,
            JsonTokenType.String => reader.GetString(),
            _ => throw new JsonException(
                $"Expected string or null for NamedRef property '{propertyName}'; got '{reader.TokenType}'."),
        };
    }
}
