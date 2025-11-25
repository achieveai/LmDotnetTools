using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.LmCore.Utils;

/// <summary>
///     A factory for creating JsonConverters for ImmutableDictionary types.
/// </summary>
public class ImmutableDictionaryJsonConverterFactory : JsonConverterFactory
{
    /// <inheritdoc />
    public override bool CanConvert(Type typeToConvert)
    {
        ArgumentNullException.ThrowIfNull(typeToConvert);

        if (!typeToConvert.IsGenericType)
        {
            return false;
        }

        var genericTypeDefinition = typeToConvert.GetGenericTypeDefinition();
        return genericTypeDefinition == typeof(ImmutableDictionary<,>);
    }

    /// <inheritdoc />
    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(typeToConvert);

        var keyType = typeToConvert.GetGenericArguments()[0];
        var valueType = typeToConvert.GetGenericArguments()[1];

        var converterType = typeof(ImmutableDictionaryJsonConverter<,>).MakeGenericType(keyType, valueType);
        return (JsonConverter)Activator.CreateInstance(converterType)!;
    }
}
