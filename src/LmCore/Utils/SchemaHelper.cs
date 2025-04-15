namespace AchieveAi.LmDotnetTools.LmCore.Utils;

using System.Collections;
using AchieveAi.LmDotnetTools.LmCore.Models;

/// <summary>
/// Helper utilities for working with JSON schema objects
/// </summary>
public static class SchemaHelper
{
    /// <summary>
    /// Creates a JsonSchemaObject from a .NET Type
    /// </summary>
    /// <param name="type">The .NET type to convert</param>
    /// <returns>A JsonSchemaObject representing the type</returns>
    public static JsonSchemaObject CreateJsonSchemaFromType(Type type)
    {
        if (type == typeof(string))
        {
            return new JsonSchemaObject { Type = "string" };
        }
        else if (type == typeof(int) || type == typeof(long) || type == typeof(short) || type == typeof(byte))
        {
            return new JsonSchemaObject { Type = "integer" };
        }
        else if (type == typeof(float) || type == typeof(double) || type == typeof(decimal))
        {
            return new JsonSchemaObject { Type = "number" };
        }
        else if (type == typeof(bool))
        {
            return new JsonSchemaObject { Type = "boolean" };
        }
        else if (type.IsArray || (type.IsGenericType && 
                (typeof(IEnumerable).IsAssignableFrom(type))))
        {
            Type elementType;
            if (type.IsArray)
            {
                elementType = type.GetElementType()!;
            }
            else
            {
                elementType = type.GetGenericArguments()[0];
            }

            return JsonSchemaObject.Array(CreateJsonSchemaFromType(elementType));
        }
        else if (type.IsEnum)
        {
            // Handle enums as strings with allowed values
            return new JsonSchemaObject { Type = "string" };
        }
        else
        {
            // Default to object for complex types
            return new JsonSchemaObject { Type = "object" };
        }
    }
} 