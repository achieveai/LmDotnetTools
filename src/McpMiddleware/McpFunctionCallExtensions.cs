using System.Reflection;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmCore.Models;
using ModelContextProtocol.Server;
using ModelContextProtocol.Utils;
using AchieveAi.LmDotnetTools.LmCore.Utils;

namespace AchieveAi.LmDotnetTools.McpMiddleware;

/// <summary>
/// Extensions for creating FunctionCallMiddleware components from MCP tool registrations
/// </summary>
public static class McpFunctionCallExtensions
{
    /// <summary>
    /// Creates a tuple containing function contracts and function map for use with FunctionCallMiddleware
    /// from an assembly containing MCP tool registrations
    /// </summary>
    /// <param name="toolAssembly">The assembly containing MCP tool types, or null to use calling assembly</param>
    /// <returns>A tuple containing function contracts and function map for FunctionCallMiddleware</returns>
    public static (IEnumerable<FunctionContract>, IDictionary<string, Func<string, Task<string>>>) CreateFunctionCallComponentsFromAssembly(
      Assembly? toolAssembly = null)
    {
        // Get the assembly to scan
        toolAssembly ??= Assembly.GetCallingAssembly();

        // Find all types with the McpServerToolTypeAttribute
        var toolTypes = toolAssembly.GetTypes()
          .Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() != null)
          .ToList();

        return CreateFunctionCallComponentsFromTypes(toolTypes);
    }

    /// <summary>
    /// Creates a tuple containing function contracts and function map for use with FunctionCallMiddleware
    /// from a collection of types with MCP tool methods
    /// </summary>
    /// <param name="toolTypes">Types that contain MCP tool methods</param>
    /// <returns>A tuple containing function contracts and function map for FunctionCallMiddleware</returns>
    public static (IEnumerable<FunctionContract>, IDictionary<string, Func<string, Task<string>>>) CreateFunctionCallComponentsFromTypes(
      IEnumerable<Type> toolTypes)
    {
        var functionContracts = new List<FunctionContract>();
        var functionMap = new Dictionary<string, Func<string, Task<string>>>();

        // Second pass: create function contracts with class name prefixes only for clashing names
        foreach (var toolType in toolTypes)
        {
            // Find all methods with McpServerToolAttribute
            var toolMethods = toolType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)
              .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() != null)
              .ToList();

            foreach (var toolMethod in toolMethods)
            {
                var toolAttr = toolMethod.GetCustomAttribute<McpServerToolAttribute>();
                if (toolAttr == null) continue;

                // Create the function contract with class name prefix if there's a name clash
                var contract = CreateFunctionContractFromToolMethod(toolMethod, toolAttr);
                functionContracts.Add(contract);

                // Create the function callback handler
                functionMap[$"{contract.ClassName}-{contract.Name}"] = async (argsJson) =>
                {
                    try
                    {
                        // Create a tool instance if needed
                        object? instance = null;
                        if (!toolMethod.IsStatic)
                        {
                            instance = Activator.CreateInstance(toolType);
                        }

                        // Parse arguments from JSON
                        var parameters = toolMethod.GetParameters();
                        object[] paramValues = new object[parameters.Length];

                        // If we have arguments to parse
                        if (!string.IsNullOrEmpty(argsJson) && parameters.Length > 0)
                        {
                            var argsDict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(argsJson);

                            for (int i = 0; i < parameters.Length; i++)
                            {
                                var param = parameters[i];
                                if (argsDict != null && argsDict.TryGetValue(param.Name!, out var argValue))
                                {
                                    paramValues[i] = JsonSerializer.Deserialize(
                              argValue.GetRawText(),
                              param.ParameterType,
                              new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
                                }
                                else if (param.HasDefaultValue)
                                {
                                    paramValues[i] = param.DefaultValue!;
                                }
                                else if (param.ParameterType.IsValueType)
                                {
                                    paramValues[i] = Activator.CreateInstance(param.ParameterType)!;
                                }
                            }
                        }

                        // Invoke the method
                        object? result;
                        if (toolMethod.ReturnType.IsAssignableTo(typeof(Task)))
                        {
                            // Handle async methods
                            dynamic task = toolMethod.Invoke(instance, paramValues)!;
                            result = await task;

                            // Handle Task<T> where T is the actual result
                            var taskType = toolMethod.ReturnType;
                            if (taskType.IsGenericType)
                            {
                                var resultType = taskType.GetGenericArguments()[0];
                                if (resultType != typeof(void))
                                {
                                    return JsonSerializer.Serialize(result);
                                }
                            }

                            // Return empty object for Task with no result
                            return "{}";
                        }
                        else
                        {
                            // Handle synchronous methods
                            result = toolMethod.Invoke(instance, paramValues);
                            if (result != null && toolMethod.ReturnType != typeof(void))
                            {
                                return JsonSerializer.Serialize(result);
                            }
                            return "{}";
                        }
                    }
                    catch (Exception ex)
                    {
                        // Return error information
                        return JsonSerializer.Serialize(new { error = ex.Message });
                    }
                };
            }
        }

        return (functionContracts, functionMap);
    }

    /// <summary>
    /// Creates a FunctionContract from an MCP tool method
    /// </summary>
    /// <param name="toolMethod">The method to create a function contract for</param>
    /// <param name="toolAttr">The MCP tool attribute on the method</param>
    /// <param name="toolType">The class type containing the method</param>
    private static FunctionContract CreateFunctionContractFromToolMethod(
      MethodInfo toolMethod,
      McpServerToolAttribute toolAttr)
    {
        var toolType = toolMethod.DeclaringType!;
        string name;

        // Handle name generation based on these rules:
        // 1. If name is explicitly provided in attribute, use it as is
        // 2. For backward compatibility with tests, if there's no clash, use the method name directly
        // 3. If there's a name clash, use TypeName.MethodName format to avoid conflicts
        if (toolAttr.Name != null)
        {
            // Use explicitly specified name from attribute
            name = toolAttr.Name;
        }
        else
        {
            // Use method name directly for backward compatibility unless there's a clash
            name = toolMethod.Name;
        }

        // Get description from System.ComponentModel.Description attribute or use default
        var descriptionAttr = toolMethod.GetCustomAttribute<System.ComponentModel.DescriptionAttribute>();
        string description = descriptionAttr?.Description
            ?? $"Tool method {toolMethod.Name} from {toolType.Name}";

        // Get parameters
        var parameters = toolMethod.GetParameters()
          .Select(p => new FunctionParameterContract
          {
              Name = p.Name!,
              Description = p.GetCustomAttribute<System.ComponentModel.DescriptionAttribute>()?.Description ?? $"Parameter {p.Name}",
              ParameterType = SchemaHelper.CreateJsonSchemaFromType(p.ParameterType),
              IsRequired = !p.HasDefaultValue && !p.IsOptional
          })
          .ToList();

        return new FunctionContract
        {
            Name = name,
            ClassName = toolType.Name,
            Namespace = toolType.Namespace,
            Description = description,
            Parameters = parameters
        };
    }

    /// <summary>
    /// Creates a FunctionCallMiddleware using tool registrations from the specified assembly
    /// </summary>
    /// <param name="toolAssembly">The assembly containing MCP tool types, or null to use calling assembly</param>
    /// <param name="name">Optional name for the middleware</param>
    /// <returns>A configured FunctionCallMiddleware</returns>
    public static FunctionCallMiddleware CreateFunctionCallMiddlewareFromAssembly(
      Assembly? toolAssembly = null,
      string? name = null)
    {
        var (functions, functionMap) = CreateFunctionCallComponentsFromAssembly(toolAssembly);
        return new FunctionCallMiddleware(functions, functionMap, name);
    }
}
