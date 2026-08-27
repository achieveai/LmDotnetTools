using System.ComponentModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Utils;

namespace AchieveAi.LmDotnetTools.LmCore.Middleware;

/// <summary>
///     Function provider that extracts functions from a type using reflection and attributes
/// </summary>
public class TypeFunctionProvider : IFunctionProvider
{
    /// <summary>
    ///     Options used to bind a single tool argument onto a method parameter.
    ///     <see cref="JsonNumberHandling.AllowReadingFromString" /> is set because models
    ///     routinely emit numeric arguments as JSON strings (<c>{"taskId":"1"}</c>).
    /// </summary>
    private static readonly JsonSerializerOptions ArgumentOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    private readonly List<FunctionDescriptor> _functions;
    private readonly object? _instance;
    private readonly Type _type;

    /// <summary>
    ///     Creates a provider from a type, using static methods only
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
    ///     Creates a provider from an instance, using instance methods only
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
                // An instance-backed provider closes over that instance, so every caller
                // sharing the provider shares its state. Static-only providers hold none.
                // Left unset this reported false, and StatelessFunctionProviderWrapper —
                // whose whole job is to keep stateful functions off a shared MCP surface —
                // would wave a per-session object through.
                IsStateful = _instance != null,
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
        if (method.GetCustomAttribute<CompilerGeneratedAttribute>() != null)
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
            ReturnType = ContractReturnType(method.ReturnType),
            ReturnDescription = returnDescription,
        };
    }

    /// <summary>
    ///     The type the caller actually receives, which is what the contract must advertise.
    ///     A method returning <see cref="FunctionResult" /> puts only its
    ///     <c>Text</c> on the wire, so the model sees a <see cref="string" />; naming the
    ///     wrapper would describe a shape that never arrives.
    /// </summary>
    private static Type? ContractReturnType(Type returnType)
    {
        if (returnType == typeof(void))
        {
            return null;
        }

        if (returnType == typeof(FunctionResult))
        {
            return typeof(string);
        }

        return returnType.IsGenericType
            && returnType.GetGenericTypeDefinition() == typeof(Task<>)
            && returnType.GetGenericArguments()[0] == typeof(FunctionResult)
            ? typeof(string)
            : returnType;
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
            var nullableAttribute = parameter.GetCustomAttribute<NullableAttribute>();
            if (nullableAttribute != null && nullableAttribute.NullableFlags.Length > 0)
            {
                // Flag 2 means nullable, 1 means not nullable
                return nullableAttribute.NullableFlags[0] == 2;
            }

            // Check the context attribute on the method or type
            var method = parameter.Member as MethodInfo;
            if (method != null)
            {
                var methodNullable = method.GetCustomAttribute<NullableContextAttribute>();
                if (methodNullable != null)
                {
                    return methodNullable.Flag == 2;
                }

                var typeNullable = method.DeclaringType?.GetCustomAttribute<NullableContextAttribute>();
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

    private ToolHandler CreateHandler(MethodInfo method)
    {
        return async (argsJson, _, _) =>
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

                if (parameters.Length > 0)
                {
                    // An absent payload is not the same as an empty one: every parameter still
                    // has to fall through to its declared default rather than staying null.
                    var argsDict = string.IsNullOrEmpty(argsJson)
                        ? null
                        : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(argsJson);

                    for (var i = 0; i < parameters.Length; i++)
                    {
                        var param = parameters[i];

                        paramValues[i] =
                            argsDict != null && argsDict.TryGetValue(param.Name!, out var argValue)
                                ? BindArgument(argValue, param.ParameterType)
                                : UnsuppliedArgument(param);
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

                // A method that opts in by returning FunctionResult can distinguish a failed
                // operation from a successful one. Only its Text is serialized, so the wire
                // shape is identical to a method that returns a plain string — what differs is
                // the IsError flag and error code carried alongside it.
                var errorCode = null as string;
                if (result is FunctionResult functionResult)
                {
                    errorCode = functionResult.ErrorCode;
                    result = functionResult.Text;
                }

                // Reflective handlers always resolve synchronously — they don't have access to a
                // ToolCallId or DeferralContext. Wrap the serialized result as Resolved.
                var serialized = result != null && method.ReturnType != typeof(void)
                    ? JsonSerializer.Serialize(
                        result,
                        new JsonSerializerOptions
                        {
                            WriteIndented = false,
                            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                        }
                    )
                    : "{}";

                return errorCode == null
                    ? ToolHandlerResult.FromText(serialized)
                    : ToolHandlerResult.FromError(serialized, errorCode);
            }
            catch (TargetInvocationException tie)
            {
                // Unwrap the real exception
                var innerException = tie.InnerException ?? tie;
                var errorJson = JsonSerializer.Serialize(
                    new { error = innerException.Message, type = innerException.GetType().Name }
                );
                return ToolHandlerResult.FromError(errorJson);
            }
            catch (Exception ex)
            {
                var errorJson = JsonSerializer.Serialize(new { error = ex.Message, type = ex.GetType().Name });
                return ToolHandlerResult.FromError(errorJson);
            }
        };
    }

    /// <summary>
    ///     Binds one JSON argument onto a parameter type, tolerating the two shapes models
    ///     get wrong most often: a quoted number for a numeric parameter, and a bare number
    ///     for a string parameter (dotted ids such as <c>"1.2"</c> are declared as strings,
    ///     so a model that sends <c>{"taskId": 1}</c> must still bind).
    /// </summary>
    private static object? BindArgument(JsonElement argValue, Type parameterType)
    {
        if (
            parameterType == typeof(string)
            && argValue.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False
        )
        {
            return argValue.GetRawText();
        }

        return JsonSerializer.Deserialize(argValue.GetRawText(), parameterType, ArgumentOptions);
    }

    /// <summary>
    ///     The value to pass for a parameter the caller did not supply. A declared C# default
    ///     wins; otherwise it is <see langword="null" /> for anything nullable and the
    ///     zero value for a non-nullable value type.
    /// </summary>
    private static object? UnsuppliedArgument(ParameterInfo param)
    {
        if (param.HasDefaultValue)
        {
            return param.DefaultValue;
        }

        return !param.ParameterType.IsValueType || Nullable.GetUnderlyingType(param.ParameterType) != null
            ? null
            : Activator.CreateInstance(param.ParameterType);
    }

    private static bool IsAsyncMethod(MethodInfo method)
    {
        return method.ReturnType == typeof(Task)
            || (method.ReturnType.IsGenericType && method.ReturnType.GetGenericTypeDefinition() == typeof(Task<>));
    }
}

/// <summary>
///     Extension methods for FunctionRegistry to easily register types and objects
/// </summary>
public static class FunctionRegistryTypeExtensions
{
    /// <summary>
    ///     Registers all eligible methods from a type as functions (static methods only)
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
        ArgumentNullException.ThrowIfNull(registry);
        var provider = new TypeFunctionProvider(type, providerName, priority);
        return registry.AddProvider(provider);
    }

    /// <summary>
    ///     Registers all eligible instance methods from an object as functions (instance methods only, not static)
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
        ArgumentNullException.ThrowIfNull(registry);
        var provider = new TypeFunctionProvider(instance, providerName, priority);
        return registry.AddProvider(provider);
    }

    /// <summary>
    ///     Registers all eligible methods from multiple types
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
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(types);

        foreach (var type in types)
        {
            _ = registry.AddFunctionsFromType(type, type.Name, priority);
        }

        return registry;
    }

    /// <summary>
    ///     Registers all types in an assembly that have at least one method with FunctionAttribute
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
