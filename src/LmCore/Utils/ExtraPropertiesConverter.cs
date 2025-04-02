using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.LmCore.Utils;

/// <summary>
/// Custom JsonConverter for handling ImmutableDictionary&lt;string, object?&gt; properties
/// specifically for the ExtraProperties field in Usage and other classes.
/// </summary>
public class ExtraPropertiesConverter : JsonConverter<ImmutableDictionary<string, object?>>
{
  public override ImmutableDictionary<string, object?> Read(
    ref Utf8JsonReader reader, 
    Type typeToConvert, 
    JsonSerializerOptions options)
  {
    if (reader.TokenType != JsonTokenType.StartObject)
    {
      throw new JsonException($"Expected {JsonTokenType.StartObject} but got {reader.TokenType}");
    }

    var builder = ImmutableDictionary.CreateBuilder<string, object?>();
    
    while (reader.Read())
    {
      if (reader.TokenType == JsonTokenType.EndObject)
      {
        return builder.ToImmutable();
      }
      
      if (reader.TokenType != JsonTokenType.PropertyName)
      {
        throw new JsonException($"Expected {JsonTokenType.PropertyName} but got {reader.TokenType}");
      }
      
      // Read property name
      string propertyName = reader.GetString()!;
      
      // Read property value
      reader.Read();
      object? value = ReadValue(ref reader, options);
      
      builder.Add(propertyName, value);
    }
    
    throw new JsonException("Expected end of object but reached end of data");
  }

  private static object? ReadValue(ref Utf8JsonReader reader, JsonSerializerOptions options)
  {
    switch (reader.TokenType)
    {
      case JsonTokenType.Null:
        return null;
        
      case JsonTokenType.False:
        return false;
        
      case JsonTokenType.True:
        return true;
        
      case JsonTokenType.String:
        return reader.GetString();
        
      case JsonTokenType.Number:
        if (reader.TryGetInt32(out int intValue))
        {
          return intValue;
        }
        if (reader.TryGetInt64(out long longValue))
        {
          return longValue;
        }
        if (reader.TryGetDouble(out double doubleValue))
        {
          return doubleValue;
        }
        return reader.GetDecimal();
        
      case JsonTokenType.StartObject:
        // For nested objects, we'll keep them as JsonElement for now
        using (var document = JsonDocument.ParseValue(ref reader))
        {
          return document.RootElement.Clone();
        }
        
      case JsonTokenType.StartArray:
        // For arrays, we'll keep them as JsonElement for now
        using (var document = JsonDocument.ParseValue(ref reader))
        {
          return document.RootElement.Clone();
        }
        
      default:
        throw new JsonException($"Unexpected token type: {reader.TokenType}");
    }
  }

  public override void Write(
    Utf8JsonWriter writer, 
    ImmutableDictionary<string, object?> value, 
    JsonSerializerOptions options)
  {
    writer.WriteStartObject();
    foreach (var kvp in value)
    {
      writer.WritePropertyName(kvp.Key);
      WriteValue(writer, kvp.Value, options);
    }
    writer.WriteEndObject();
  }
  
  private static void WriteValue(Utf8JsonWriter writer, object? value, JsonSerializerOptions options)
  {
    if (value == null)
    {
      writer.WriteNullValue();
      return;
    }
    
    switch (value)
    {
      case string stringValue:
        writer.WriteStringValue(stringValue);
        break;
        
      case bool boolValue:
        writer.WriteBooleanValue(boolValue);
        break;
        
      case int intValue:
        writer.WriteNumberValue(intValue);
        break;
        
      case long longValue:
        writer.WriteNumberValue(longValue);
        break;
        
      case double doubleValue:
        writer.WriteNumberValue(doubleValue);
        break;
        
      case decimal decimalValue:
        writer.WriteNumberValue(decimalValue);
        break;
        
      case JsonElement jsonElement:
        jsonElement.WriteTo(writer);
        break;
        
      default:
        // For other types, use the default serialization
        JsonSerializer.Serialize(writer, value, options);
        break;
    }
  }
}
