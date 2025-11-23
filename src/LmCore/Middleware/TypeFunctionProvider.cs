using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Utils;

namespace AchieveAi.LmDotnetTools.LmCore.Middleware;

/// <summary>
/// Function provider that extracts functions from a type using reflection and attributes
/// </summary>
public class TypeFunctionProvider : IFunctionProvider
{
    private readonly Type _type;
    private readonly object? _instance;
    private readonly List<FunctionDescriptor> _functions;

    /// <summary>
    /// Creates a provider from a type, using static methods only
    /// </summary>
    public TypeFunctionProvider(Type type, string? providerName = null, int priority = 100)
    {
        _type = type ?? throw new ArgumentNullException(nameof(type));
        _instance = null;
        ProviderName = providerName ?? type.Name;
        Priority = priority;
        _functions = ExtractFunctions();
    }

    /// <summary>
    /// Creates a provider from an instance, using instance methods only
    /// </summary>
    public TypeFunctionProvider(object instance, string? providerName = null, int priority = 100)
    {
        _instance = instance ?? throw new ArgumentNullException(nameof(instance));
        _type = instance.GetType();
        ProviderName = providerName ?? _type.Name;
        Priority = priority;
        _functions = ExtractFunctions();
    }

    public string ProviderName { get; }
    public int Priority { get; }

    public IEnumerable<FunctionDescriptor> GetFunctions()
    {
        return _functions;
    }

    private List<FunctionDescriptor> ExtractFunctions()
    {
        var functions = new List<FunctionDescriptor>();
        var bindingFlags = BindingFlags.Public | BindingFlags.NonPublic;

        // If we have an instance, only include instance methods
        // If we don't have an instance (type-only), only include static methods
        if (_instance != null)
        {
            bindingFlags |= BindingFlags.Instance;
        }
        else
        {
            bindingFlags |= BindingFlags.Static;
        }

        var methods = _type.GetMethods(bindingFlags).Where(ShouldIncludeMethod).ToList();

        foreach (var method in methods)
        {
            var contract = CreateFunctionContract(method);
            var handler = CreateHandler(method);

            var descriptor = new FunctionDescriptor
            {
                Contract = contract,
                Handler = handler,
                ProviderName = ProviderName,
            };

            functions.Add(descriptor);
        }

        return functions;
    }

    private bool ShouldIncludeMethod(MethodInfo method)
    {
        // Skip special methods
        if (method.IsSpecialName || method.IsConstructor)
        {
            return false;
        }

        // Skip property getters/setters
        if (method.Name.StartsWith("get_") || method.Name.StartsWith("set_"))
        {
            return false;
        }

        // Skip compiler-generated methods
        if (method.GetCustomAttribute<System.Runtime.CompilerServices.CompilerGeneratedAttribute>() != null)
        {
            return false;
        }

        // Include if it has FunctionAttribute
        if (method.GetCustomAttribute<FunctionAttribute>() != null)
        {
            return true;
        }

        // Include if it has DescriptionAttribute (opt-in via description)
        if (method.GetCustomAttribute<DescriptionAttribute>() != null)
        {
            return true;
        }

        // Skip everything else
        return false;
    }

    private FunctionContract CreateFunctionContract(MethodInfo method)
    {
        // Get function name from attribute or use method name
        var functionAttr = method.GetCustomAttribute<FunctionAttribute>();
        var name = functionAttr?.FunctionName ?? method.Name;

        // Get description from FunctionAttribute or DescriptionAttribute
        var description =
            functionAttr?.Description
            ?? method.GetCustomAttribute<DescriptionAttribute>()?.Description
            ?? $"Executes {method.Name}";

        // Extract parameters
        var parameters = method.GetParameters().Select(CreateParameterContract).ToList();

        // Get return type description if available
        var returnDescription = method.ReturnParameter?.GetCustomAttribute<DescriptionAttribute>()?.Description;

        return new FunctionContract
        {
            Name = name,
            Description = description,
            Parameters = parameters,
            ReturnType = method.ReturnType != typeof(void) ? method.ReturnType : null,
            ReturnDescription = returnDescription,
        };
    }

    private FunctionParameterContract CreateParameterContract(ParameterInfo parameter)
    {
        var description =
            parameter.GetCustomAttribute<DescriptionAttribute>()?.Description ?? $"Parameter {parameter.Name}";

        return new FunctionParameterContract
        {
            Name = parameter.Name!,
            Description = description,
            ParameterType = SchemaHelper.CreateJsonSchemaFromType(parameter.ParameterType),
            IsRequired = !parameter.HasDefaultValue && !IsNullable(parameter),
        };
    }

    private static bool IsNullable(ParameterInfo parameter)
    {
        var paramType = parameter.ParameterType;

        // Check for Nullable<T> value types
        if (Nullable.GetUnderlyingType(paramType) != null)
        {
            return true;
        }

        // For reference types, check nullability annotations
        if (!paramType.IsValueType)
        {
            // Check for nullable reference type annotations
            var nullableAttribute = parameter.GetCustomAttribute<System.Runtime.CompilerServices.NullableAttribute>();
            if (nullableAttribute != null && nullableAttribute.NullableFlags.Length > 0)
            {
                // Flag 2 means nullable, 1 means not nullable
                return nullableAttribute.NullableFlags[0] == 2;
            }

            // Check the context attribute on the method or type
            var method = parameter.Member as MethodInfo;
            if (method != null)
            {
                var methodNullable =
                    method.GetCustomAttribute<System.Runtime.CompilerServices.NullableContextAttribute>();
                if (methodNullable != null)
                {
                    return methodNullable.Flag == 2;
                }

                var typeNullable =
                    method.DeclaringType?.GetCustomAttribute<System.Runtime.CompilerServices.NullableContextAttribute>();
                if (typeNullable != null)
                {
                    return typeNullable.Flag == 2;
                }
            }

            // Default to non-nullable for reference types without annotations
            return false;
        }

        return false;
    }

    private Func<string, Task<string>> CreateHandler(MethodInfo method)
    {
        return async (argsJson) =>
        {
            try
            {
                // Get the instance to invoke on
                var target = method.IsStatic ? null : _instance;

                if (!method.IsStatic && target == null)
                {
                    throw new InvalidOperationException(
                        $"Cannot invoke instance method {method.Name} without an instance"
                    );
                }

                // Parse and prepare arguments
                var parameters = method.GetParameters();
                var paramValues = new object?[parameters.Length];

                if (!string.IsNullOrEmpty(argsJson) && parameters.Length > 0)
                {
                    var argsDict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(argsJson);

                    for (var i = 0; i < parameters.Length; i++)
                    {
                        var param = parameters[i];

                        if (argsDict != null && argsDict.TryGetValue(param.Name!, out var argValue))
                        {
                            // Deserialize the argument
                            paramValues[i] = JsonSerializer.Deserialize(
                                argValue.GetRawText(),
                                param.ParameterType,
                                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
                            );
                        }
                        else if (param.HasDefaultValue)
                        {
                            paramValues[i] = param.DefaultValue;
                        }
                        else
                        {
                            paramValues[i] = !param.ParameterType.IsValueType
                            || Nullable.GetUnderlyingType(param.ParameterType) != null
                                ? null
                                : Activator.CreateInstance(param.ParameterType);
                        }
                    }
                }

                // Invoke the method
                object? result;

                if (IsAsyncMethod(method))
                {
                    // Handle async methods
                    var task = method.Invoke(target, paramValues);

                    if (task is Task nonGenericTask)
                    {
                        await nonGenericTask.ConfigureAwait(false);

                        // Check if it's Task<T>
                        if (method.ReturnType.IsGenericType)
                        {
                            var resultProperty = task.GetType().GetProperty("Result");
                            result = resultProperty?.GetValue(task);
                        }
                        else
                        {
                            result = null;
                        }
                    }
                    else
                    {
                        throw new InvalidOperationException($"Async method {method.Name} did not return a Task");
                    }
                }
                else
                {
                    // Handle synchronous methods
                    result = method.Invoke(target, paramValues);
                }

                // Serialize the result
                return result != null && method.ReturnType != typeof(void)
                    ? JsonSerializer.Serialize(
                        result,
                        new JsonSerializerOptions
                        {
                            WriteIndented = false,
                            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                        }
                    )
                    : "{}";
            }
            catch (TargetInvocationException tie)
            {
                // Unwrap the real exception
                var innerException = tie.InnerException ?? tie;
                return JsonSerializer.Serialize(
                    new { error = innerException.Message, type = innerException.GetType().Name }
                );
            }
            catch (Exception ex)
            {
                return JsonSerializer.Serialize(new { error = ex.Message, type = ex.GetType().Name });
            }
        };
    }

    private static bool IsAsyncMethod(MethodInfo method)
    {
        return method.ReturnType == typeof(Task)
            || (method.ReturnType.IsGenericType && method.ReturnType.GetGenericTypeDefinition() == typeof(Task<>));
    }
}

/// <summary>
/// Extension methods for FunctionRegistry to easily register types and objects
/// </summary>
public static class FunctionRegistryTypeExtensions
{
    /// <summary>
    /// Registers all eligible methods from a type as functions (static methods only)
    /// </summary>
    /// <param name="registry">The function registry</param>
    /// <param name="type">The type to register functions from</param>
    /// <param name="providerName">Optional provider name</param>
    /// <param name="priority">Provider priority (default 100)</param>
    /// <returns>The registry for chaining</returns>
    public static FunctionRegistry AddFunctionsFromType(
        this FunctionRegistry registry,
        Type type,
        string? providerName = null,
        int priority = 100
    )
    {
        var provider = new TypeFunctionProvider(type, providerName, priority);
        return registry.AddProvider(provider);
    }

    /// <summary>
    /// Registers all eligible instance methods from an object as functions (instance methods only, not static)
    /// </summary>
    /// <param name="registry">The function registry</param>
    /// <param name="instance">The object instance to register functions from</param>
    /// <param name="providerName">Optional provider name</param>
    /// <param name="priority">Provider priority (default 100)</param>
    /// <returns>The registry for chaining</returns>
    public static FunctionRegistry AddFunctionsFromObject(
        this FunctionRegistry registry,
        object instance,
        string? providerName = null,
        int priority = 100
    )
    {
        var provider = new TypeFunctionProvider(instance, providerName, priority);
        return registry.AddProvider(provider);
    }

    /// <summary>
    /// Registers all eligible methods from multiple types
    /// </summary>
    /// <param name="registry">The function registry</param>
    /// <param name="types">The types to register functions from</param>
    /// <param name="priority">Provider priority (default 100)</param>
    /// <returns>The registry for chaining</returns>
    public static FunctionRegistry AddFunctionsFromTypes(
        this FunctionRegistry registry,
        IEnumerable<Type> types,
        int priority = 100
    )
    {
        foreach (var type in types)
        {
            _ = registry.AddFunctionsFromType(type, type.Name, priority);
        }
        return registry;
    }

    /// <summary>
    /// Registers all types in an assembly that have at least one method with FunctionAttribute
    /// </summary>
    /// <param name="registry">The function registry</param>
    /// <param name="assembly">The assembly to scan (null for calling assembly)</param>
    /// <param name="priority">Provider priority (default 100)</param>
    /// <returns>The registry for chaining</returns>
    public static FunctionRegistry AddFunctionsFromAssembly(
        this FunctionRegistry registry,
        Assembly? assembly = null,
        int priority = 100
    )
    {
        assembly ??= Assembly.GetCallingAssembly();

        var typesWithFunctions = assembly
            .GetTypes()
            .Where(t => !t.IsAbstract && !t.IsInterface)
            .Where(t =>
                t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)
                    .Any(m =>
                        m.GetCustomAttribute<FunctionAttribute>() != null
                        || m.GetCustomAttribute<DescriptionAttribute>() != null
                    )
            )
            .ToList();

        return registry.AddFunctionsFromTypes(typesWithFunctions, priority);
    }
}
