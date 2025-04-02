using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Models;

namespace AchieveAi.LmDotnetTools.OpenAIProvider.Models;

public static class FunctionContractExtension
{
    /// <summary>
    /// Convert a <see cref="FunctionContract"/> to a <see cref="FunctionDefinition"/> that can be used in function call.
    /// </summary>
    /// <param name="functionContract">function contract</param>
    /// <returns><see cref="FunctionDefinition"/></returns>
    public static FunctionDefinition ToOpenFunctionDefinition(this FunctionContract functionContract)
    {
        var name = functionContract.Name 
            ?? throw new Exception("Function name cannot be null");
        var description = functionContract.Description 
            ?? throw new Exception("Function description cannot be null");

        var schemaBuilder = JsonSchemaObject.Create()
            .WithDescription($"Parameters for {name}");

        if (functionContract.Parameters != null)
        {
            foreach (var param in functionContract.Parameters)
            {
                if (param.Name is null)
                {
                    throw new InvalidOperationException("Parameter name cannot be null");
                }

                if (param.ParameterType is null)
                {
                    throw new ArgumentNullException(nameof(param.ParameterType));
                }

                // Convert the parameter type to an appropriate JsonSchemaProperty
                var property = CreatePropertyForType(param.ParameterType, param.Description);
                
                schemaBuilder = schemaBuilder.WithProperty(param.Name, property, param.IsRequired);
            }
        }

        var parameters = schemaBuilder.Build();
        
        return new FunctionDefinition(name, description, parameters);
    }

    /// <summary>
    /// Creates a JsonSchemaProperty based on the .NET type
    /// </summary>
    private static JsonSchemaProperty CreatePropertyForType(Type type, string? description)
    {
        // Handle primitive types
        if (type == typeof(string))
        {
            return JsonSchemaProperty.String(description);
        }
        else if (type == typeof(int) || type == typeof(long) || type == typeof(short) || type == typeof(byte))
        {
            return JsonSchemaProperty.Integer(description);
        }
        else if (type == typeof(float) || type == typeof(double) || type == typeof(decimal))
        {
            return JsonSchemaProperty.Number(description);
        }
        else if (type == typeof(bool))
        {
            return JsonSchemaProperty.Boolean(description);
        }
        // Handle arrays and collections
        else if (type.IsArray || (type.IsGenericType && 
                (typeof(IEnumerable<>).IsAssignableFrom(type.GetGenericTypeDefinition()) ||
                 typeof(ICollection<>).IsAssignableFrom(type.GetGenericTypeDefinition()) ||
                 typeof(IList<>).IsAssignableFrom(type.GetGenericTypeDefinition()))))
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

            var itemsProperty = CreatePropertyForType(elementType, null);
            
            // Create schema for array items
            var itemsSchema = new JsonSchemaObject
            {
                Type = itemsProperty.Type,
                Description = itemsProperty.Description
                // Add other properties as needed
            };

            return JsonSchemaProperty.Array(itemsSchema, description);
        }
        // For objects and other complex types, use a generic object schema
        else
        {
            // For complex objects, we'd ideally extract the properties via reflection
            // But for simplicity, we'll just use a generic object type
            return new JsonSchemaProperty
            {
                Type = "object",
                Description = description
            };
        }
    }
}