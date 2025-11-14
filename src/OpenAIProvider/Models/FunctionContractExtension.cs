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
        var name = functionContract.Name ?? throw new Exception("Function name cannot be null");
        var description = functionContract.Description ?? throw new Exception("Function description cannot be null");

        var schemaBuilder = JsonSchemaObject.Create().WithDescription($"Parameters for {name}");

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

                // Convert the parameter type to an appropriate JsonSchemaObject
                var property = CreatePropertyForType(param.ParameterType, param.Description);

                schemaBuilder = schemaBuilder.WithProperty(param.Name, property, param.IsRequired);
            }
        }

        var parameters = schemaBuilder.Build();

        return new FunctionDefinition(name, description, parameters);
    }

    /// <summary>
    /// Creates a JsonSchemaObject based on the .NET type
    /// </summary>
    private static JsonSchemaObject CreatePropertyForType(JsonSchemaObject schemaObject, string? description)
    {
        // Handle based on JsonSchemaObject type
        return JsonSchemaObject.GetJsonPrimaryType(schemaObject) switch
        {
            "string" => JsonSchemaObject.String(description),
            "integer" => JsonSchemaObject.Integer(description),
            "number" => JsonSchemaObject.Number(description),
            "boolean" => JsonSchemaObject.Boolean(description),
            "array" when schemaObject.Items != null => JsonSchemaObject.Array(schemaObject.Items, description),
            "object" => new JsonSchemaObject
            {
                Type = JsonSchemaTypeHelper.ToType(["object", "null"]),
                Description = description,
            },
            _ => new JsonSchemaObject
            {
                Type = JsonSchemaTypeHelper.ToType(["string", "null"]),
                Description = description,
            },
        };
    }
}
