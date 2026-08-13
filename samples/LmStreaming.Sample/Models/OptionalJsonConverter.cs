using System.Text.Json;
using System.Text.Json.Serialization;

namespace LmStreaming.Sample.Models;

/// <summary>Factory for <see cref="OptionalJsonConverter{T}"/>, resolving the closed generic per property.</summary>
public sealed class OptionalJsonConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert)
    {
        ArgumentNullException.ThrowIfNull(typeToConvert);

        return typeToConvert.IsGenericType && typeToConvert.GetGenericTypeDefinition() == typeof(Optional<>);
    }

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(typeToConvert);

        var innerType = typeToConvert.GetGenericArguments()[0];
        var converterType = typeof(OptionalJsonConverter<>).MakeGenericType(innerType);
        return (JsonConverter)Activator.CreateInstance(converterType)!;
    }
}

/// <summary>
/// Reads/writes <see cref="Optional{T}"/>. Read is only ever invoked by <see cref="System.Text.Json"/>
/// when the property is PRESENT in the JSON (including explicit <c>null</c>) — an omitted property
/// leaves the field at its default (<c>Optional&lt;T&gt;.Unset</c>), which is exactly what distinguishes
/// "unchanged" from "explicit null".
/// </summary>
public sealed class OptionalJsonConverter<T> : JsonConverter<Optional<T>>
{
    public override Optional<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var value = JsonSerializer.Deserialize<T>(ref reader, options);
        return new Optional<T>(value);
    }

    public override void Write(Utf8JsonWriter writer, Optional<T> value, JsonSerializerOptions options)
    {
        JsonSerializer.Serialize(writer, value.Value, options);
    }
}
