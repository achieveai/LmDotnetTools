using System.ComponentModel;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Core;
namespace AchieveAi.LmDotnetTools.LmCore.Tests.Middleware;

public class TypeFunctionProviderTests
{
    [Fact]
    public void TypeProvider_WithStaticType_ExtractsStaticMethodsOnly()
    {
        // Arrange
        var provider = new TypeFunctionProvider(typeof(TestHandlerWithFunctionAttribute));

        // Act
        var functions = provider.GetFunctions().ToList();

        // Assert
        _ = Assert.Single(functions);
        var multiplyFunc = functions.First(f => f.Contract.Name == "multiply");
        Assert.NotNull(multiplyFunc);
        Assert.Equal("Multiplies two numbers", multiplyFunc.Contract.Description);
    }

    [Fact]
    public void TypeProvider_WithInstance_ExtractsInstanceMethodsOnly()
    {
        // Arrange
        var instance = new TestHandlerWithFunctionAttribute();
        var provider = new TypeFunctionProvider(instance);

        // Act
        var functions = provider.GetFunctions().ToList();

        // Assert
        Assert.Equal(2, functions.Count); // Only 'add' and 'AsyncMethod', not static 'multiply'
        Assert.Contains(functions, f => f.Contract.Name == "add");
        Assert.Contains(functions, f => f.Contract.Name == "AsyncMethod");
        Assert.DoesNotContain(functions, f => f.Contract.Name == "multiply"); // Static method excluded
    }

    [Fact]
    public void TypeProvider_ExtractsDescriptionAttributes()
    {
        // Arrange - Test with type (static methods only)
        var provider = new TypeFunctionProvider(typeof(TestHandlerWithDescriptionAttribute));

        // Act
        var functions = provider.GetFunctions().ToList();

        // Assert
        _ = Assert.Single(functions); // Only static GetLength
        var getLengthFunc = functions.First();
        Assert.Equal("GetLength", getLengthFunc.Contract.Name);
        Assert.Equal("Gets the length of a string", getLengthFunc.Contract.Description);
    }

    [Fact]
    public void TypeProvider_WithInstance_ExtractsInstanceDescriptionAttributes()
    {
        // Arrange - Test with instance (instance methods only)
        var instance = new TestHandlerWithDescriptionAttribute();
        var provider = new TypeFunctionProvider(instance);

        // Act
        var functions = provider.GetFunctions().ToList();

        // Assert
        _ = Assert.Single(functions); // Only instance Concat
        var concatFunc = functions.First();
        Assert.Equal("Concat", concatFunc.Contract.Name);
        Assert.Equal("Concatenates two strings", concatFunc.Contract.Description);
    }

    [Fact]
    public async Task TypeProvider_HandlerExecutesCorrectly()
    {
        // Arrange
        var instance = new TestHandlerWithFunctionAttribute();
        var provider = new TypeFunctionProvider(instance);
        var addFunction = provider.GetFunctions().First(f => f.Contract.Name == "add");

        // Act
        var args = JsonSerializer.Serialize(new { a = 5, b = 3 });
        var result = await addFunction.Handler(args, new ToolCallContext(), CancellationToken.None);
        var resultValue = JsonSerializer.Deserialize<int>(result.ResultText);

        // Assert
        Assert.Equal(8, resultValue);
    }

    [Fact]
    public async Task TypeProvider_StaticHandlerExecutesCorrectly()
    {
        // Arrange
        var provider = new TypeFunctionProvider(typeof(TestHandlerWithFunctionAttribute));
        var multiplyFunction = provider.GetFunctions().First(f => f.Contract.Name == "multiply");

        // Act
        var args = JsonSerializer.Serialize(new { x = 4, y = 7 });
        var result = await multiplyFunction.Handler(args, new ToolCallContext(), CancellationToken.None);
        var resultValue = JsonSerializer.Deserialize<int>(result.ResultText);

        // Assert
        Assert.Equal(28, resultValue);
    }

    [Fact]
    public async Task TypeProvider_AsyncMethodExecutesCorrectly()
    {
        // Arrange
        var instance = new TestHandlerWithFunctionAttribute();
        var provider = new TypeFunctionProvider(instance);
        var asyncFunction = provider.GetFunctions().First(f => f.Contract.Name == "AsyncMethod");

        // Act
        var args = JsonSerializer.Serialize(new { input = "test" });
        var result = await asyncFunction.Handler(args, new ToolCallContext(), CancellationToken.None);
        var resultValue = JsonSerializer.Deserialize<string>(result.ResultText);

        // Assert
        Assert.Equal("Processed: test", resultValue);
    }

    [Fact]
    public async Task TypeProvider_HandlesDefaultParameters()
    {
        // Arrange
        var instance = new TestHandlerMixed();
        var provider = new TypeFunctionProvider(instance);
        var calculateFunction = provider.GetFunctions().First(f => f.Contract.Name == "calculate");

        // Act - Call without factor parameter (should use default)
        var args = JsonSerializer.Serialize(new { value = 10.0 });
        var result = await calculateFunction.Handler(args, new ToolCallContext(), CancellationToken.None);
        var resultValue = JsonSerializer.Deserialize<double>(result.ResultText);

        // Assert
        Assert.Equal(20.0, resultValue);
    }

    [Fact]
    public async Task TypeProvider_HandlesNullableParameters()
    {
        // Arrange
        var instance = new TestHandlerMixed();
        var provider = new TypeFunctionProvider(instance);
        var toUpperFunction = provider.GetFunctions().First(f => f.Contract.Name == "ToUpper");

        // Act - Call with null
        var args = "{}"; // Empty args, text will be null
        var result = await toUpperFunction.Handler(args, new ToolCallContext(), CancellationToken.None);
        var resultValue = JsonSerializer.Deserialize<string>(result.ResultText);

        // Assert
        Assert.Equal(string.Empty, resultValue);
    }

    [Fact]
    public async Task TypeProvider_MaintainsInstanceState()
    {
        // Arrange
        var instance = new TestHandlerMixed();
        var provider = new TypeFunctionProvider(instance);
        var incrementFunction = provider.GetFunctions().First(f => f.Contract.Name == "increment");

        // Act - Call multiple times
        var result1 = await incrementFunction.Handler("{}", new ToolCallContext(), CancellationToken.None);
        var result2 = await incrementFunction.Handler("{}", new ToolCallContext(), CancellationToken.None);
        var result3 = await incrementFunction.Handler("{}", new ToolCallContext(), CancellationToken.None);

        // Assert
        Assert.Equal(1, JsonSerializer.Deserialize<int>(result1.ResultText));
        Assert.Equal(2, JsonSerializer.Deserialize<int>(result2.ResultText));
        Assert.Equal(3, JsonSerializer.Deserialize<int>(result3.ResultText));
    }

    [Fact]
    public async Task TypeProvider_HandlesExceptions()
    {
        // Arrange
        var instance = new TestHandlerWithExceptions();
        var provider = new TypeFunctionProvider(instance);
        var divideFunction = provider.GetFunctions().First(f => f.Contract.Name == "divide");

        // Act
        var args = JsonSerializer.Serialize(new { a = 10.0, b = 0.0 });
        var result = await divideFunction.Handler(args, new ToolCallContext(), CancellationToken.None);
        var errorResult = JsonSerializer.Deserialize<Dictionary<string, string>>(result.ResultText);

        // Assert
        Assert.NotNull(errorResult);
        Assert.True(errorResult.ContainsKey("error"));
        Assert.Contains("Cannot divide by zero", errorResult["error"]);
        Assert.True(errorResult.ContainsKey("type"));
    }

    [Fact]
    public async Task TypeProvider_HandlesAsyncExceptions()
    {
        // Arrange
        var instance = new TestHandlerWithExceptions();
        var provider = new TypeFunctionProvider(instance);
        var asyncErrorFunction = provider.GetFunctions().First(f => f.Contract.Name == "asyncError");

        // Act
        var result = await asyncErrorFunction.Handler("{}", new ToolCallContext(), CancellationToken.None);
        var errorResult = JsonSerializer.Deserialize<Dictionary<string, string>>(result.ResultText);

        // Assert
        Assert.NotNull(errorResult);
        Assert.True(errorResult.ContainsKey("error"));
        Assert.Contains("Async error occurred", errorResult["error"]);
    }

    [Fact]
    public void FunctionRegistryExtensions_AddFunctionsFromType()
    {
        // Arrange
        var registry = new FunctionRegistry();

        // Act
        _ = registry.AddFunctionsFromType(typeof(TestHandlerWithFunctionAttribute));
        var (contracts, handlers) = registry.Build();

        // Assert
        _ = Assert.Single(contracts);
        _ = Assert.Single(handlers);
        Assert.Contains(contracts, c => c.Name == "multiply");
    }

    [Fact]
    public void FunctionRegistryExtensions_AddFunctionsFromObject()
    {
        // Arrange
        var registry = new FunctionRegistry();
        var instance = new TestHandlerWithFunctionAttribute();

        // Act
        _ = registry.AddFunctionsFromObject(instance);
        var (contracts, handlers) = registry.Build();

        // Assert - Only instance methods (add, AsyncMethod), not static (multiply)
        Assert.Equal(2, contracts.Count());
        Assert.Equal(2, handlers.Count);
        Assert.Contains(contracts, c => c.Name == "add");
        Assert.Contains(contracts, c => c.Name == "AsyncMethod");
        Assert.DoesNotContain(contracts, c => c.Name == "multiply");
    }

    [Fact]
    public void FunctionRegistryExtensions_AddFunctionsFromTypes()
    {
        // Arrange
        var registry = new FunctionRegistry();
        var types = new[] { typeof(TestHandlerWithFunctionAttribute), typeof(TestHandlerWithDescriptionAttribute) };

        // Act
        _ = registry.AddFunctionsFromTypes(types);
        var (contracts, handlers) = registry.Build();

        // Assert
        Assert.Equal(2, contracts.Count()); // 1 static from each type
        Assert.Equal(2, handlers.Count);
    }

    [Fact]
    public async Task FunctionRegistryExtensions_IntegrationWithMiddleware()
    {
        // Arrange
        var registry = new FunctionRegistry();
        var instance = new TestHandlerMixed();

        _ = registry.AddFunctionsFromObject(instance);
        var middleware = registry.BuildMiddleware("TestMiddleware");

        // Act - verify middleware was created with functions
        var (contracts, handlers) = registry.Build();

        // Assert - TestHandlerMixed has 3 instance methods (calculate, ToUpper, increment)
        Assert.NotNull(middleware);
        Assert.Equal(3, contracts.Count());

        // Test execution through handler
        var calculateHandler = handlers["calculate"];
        var result = await calculateHandler(JsonSerializer.Serialize(new { value = 5.0, factor = 3.0 }), new ToolCallContext(), CancellationToken.None);
        Assert.Equal(15.0, JsonSerializer.Deserialize<double>(result.ResultText));
    }

    [Fact]
    public void TypeProvider_ExtractsParameterDescriptions()
    {
        // Arrange
        var provider = new TypeFunctionProvider(typeof(TestHandlerWithFunctionAttribute));

        // Act
        var functions = provider.GetFunctions().ToList();
        var multiplyFunc = functions.First(f => f.Contract.Name == "multiply");

        // Assert
        Assert.NotNull(multiplyFunc.Contract.Parameters);
        Assert.Equal(2, multiplyFunc.Contract.Parameters.Count());
        var firstParam = multiplyFunc.Contract.Parameters?.First(p => p.Name == "x");
        Assert.NotNull(firstParam);
        Assert.Equal("First number", firstParam.Description);
        var secondParam = multiplyFunc.Contract.Parameters?.First(p => p.Name == "y");
        Assert.NotNull(secondParam);
        Assert.Equal("Second number", secondParam.Description);
    }

    [Fact]
    public void TypeProvider_IdentifiesRequiredParameters()
    {
        // Arrange
        var instance = new TestHandlerMixed();
        var provider = new TypeFunctionProvider(instance);

        // Act
        var functions = provider.GetFunctions().ToList();
        var calculateFunc = functions.First(f => f.Contract.Name == "calculate");
        var toUpperFunc = functions.First(f => f.Contract.Name == "ToUpper");

        // Assert
        // 'value' is required, 'factor' has default
        var valueParam = calculateFunc.Contract.Parameters?.First(p => p.Name == "value");
        var factorParam = calculateFunc.Contract.Parameters?.First(p => p.Name == "factor");
        Assert.NotNull(valueParam);
        Assert.NotNull(factorParam);
        Assert.True(valueParam.IsRequired);
        Assert.False(factorParam.IsRequired);

        // 'text' is nullable, so not required
        var textParam = toUpperFunc.Contract.Parameters?.First(p => p.Name == "text");
        Assert.NotNull(textParam);
        Assert.False(textParam.IsRequired);
    }

    #region Argument Binding Tests

    [Fact]
    public async Task Handler_QuotedNumber_BindsToIntParameter()
    {
        // Arrange - LLMs routinely emit numeric arguments as JSON strings.
        var provider = new TypeFunctionProvider(new TestHandlerBinding());
        var function = provider.GetFunctions().First(f => f.Contract.Name == "bind-int");

        // Act
        var result = await function.Handler(
            """{"taskId":"1"}""",
            new ToolCallContext(),
            CancellationToken.None
        );

        // Assert
        Assert.False(result is ToolHandlerResult.Resolved { Payload.IsError: true });
        Assert.Equal("int:1", JsonSerializer.Deserialize<string>(result.ResultText));
    }

    [Fact]
    public async Task Handler_UnquotedNumber_BindsToStringParameter()
    {
        // Arrange - the mirror case: a string parameter (dotted ids like "1.2")
        // receiving a bare JSON number.
        var provider = new TypeFunctionProvider(new TestHandlerBinding());
        var function = provider.GetFunctions().First(f => f.Contract.Name == "bind-string");

        // Act
        var result = await function.Handler("""{"taskId":1}""", new ToolCallContext(), CancellationToken.None);

        // Assert
        Assert.False(result is ToolHandlerResult.Resolved { Payload.IsError: true });
        Assert.Equal("string:1", JsonSerializer.Deserialize<string>(result.ResultText));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public async Task Handler_EmptyArgumentPayload_AppliesDeclaredDefaults(string? argsJson)
    {
        // Arrange - an empty/null payload must still honour the C# default.
        var provider = new TypeFunctionProvider(new TestHandlerBinding());
        var function = provider.GetFunctions().First(f => f.Contract.Name == "bind-defaulted");

        // Act
        var result = await function.Handler(argsJson!, new ToolCallContext(), CancellationToken.None);

        // Assert
        Assert.False(result is ToolHandlerResult.Resolved { Payload.IsError: true });
        Assert.Equal("limit:7,flag:False", JsonSerializer.Deserialize<string>(result.ResultText));
    }

    [Fact]
    public async Task Handler_EmptyArgumentPayload_SuppliesValueTypeDefaults()
    {
        // Arrange - a value-type parameter with no C# default must still bind.
        var provider = new TypeFunctionProvider(new TestHandlerBinding());
        var function = provider.GetFunctions().First(f => f.Contract.Name == "bind-required-flag");

        // Act
        var result = await function.Handler("", new ToolCallContext(), CancellationToken.None);

        // Assert
        Assert.False(result is ToolHandlerResult.Resolved { Payload.IsError: true });
        Assert.Equal("required:False", JsonSerializer.Deserialize<string>(result.ResultText));
    }

    [Fact]
    public void InstanceProvider_MarksFunctionsStateful()
    {
        // An instance-backed provider closes over that instance; sharing the provider
        // shares its state. StatelessFunctionProviderWrapper filters on exactly this flag.
        var provider = new TypeFunctionProvider(new TestHandlerBinding());

        Assert.All(provider.GetFunctions(), f => Assert.True(f.IsStateful));
    }

    [Fact]
    public void StaticProvider_MarksFunctionsStateless()
    {
        var provider = new TypeFunctionProvider(typeof(TestHandlerWithFunctionAttribute));

        Assert.All(provider.GetFunctions(), f => Assert.False(f.IsStateful));
    }

    #endregion

    #region Error Signalling Tests

    [Fact]
    public async Task Handler_PlainStringReturn_IsDeliveredAsSuccess()
    {
        // A method that has not opted in must be unaffected, even when its text says "Error".
        var provider = new TypeFunctionProvider(new TestHandlerErrorSignalling());
        var function = provider.GetFunctions().First(f => f.Contract.Name == "legacy-error");

        var result = await function.Handler("{}", new ToolCallContext(), CancellationToken.None);

        var resolved = Assert.IsType<ToolHandlerResult.Resolved>(result);
        Assert.False(resolved.Payload.IsError);
        Assert.Null(resolved.Payload.ErrorCode);
        Assert.Equal("Error: not found.", JsonSerializer.Deserialize<string>(resolved.Payload.Text));
    }

    [Fact]
    public async Task Handler_FunctionResultError_IsDeliveredAsErrorWithCode()
    {
        var provider = new TypeFunctionProvider(new TestHandlerErrorSignalling());
        var function = provider.GetFunctions().First(f => f.Contract.Name == "signalled-error");

        var result = await function.Handler("{}", new ToolCallContext(), CancellationToken.None);

        var resolved = Assert.IsType<ToolHandlerResult.Resolved>(result);
        Assert.True(resolved.Payload.IsError);
        Assert.Equal("thing_not_found", resolved.Payload.ErrorCode);
        // Only Text is serialized, so the wire shape matches a plain string return.
        Assert.Equal("Error: not found.", JsonSerializer.Deserialize<string>(resolved.Payload.Text));
    }

    [Fact]
    public async Task Handler_FunctionResultSuccess_IsDeliveredAsSuccess()
    {
        var provider = new TypeFunctionProvider(new TestHandlerErrorSignalling());
        var function = provider.GetFunctions().First(f => f.Contract.Name == "signalled-ok");

        var result = await function.Handler("{}", new ToolCallContext(), CancellationToken.None);

        var resolved = Assert.IsType<ToolHandlerResult.Resolved>(result);
        Assert.False(resolved.Payload.IsError);
        Assert.Null(resolved.Payload.ErrorCode);
        Assert.Equal("all good", JsonSerializer.Deserialize<string>(resolved.Payload.Text));
    }

    [Fact]
    public void FunctionResult_ImplicitlyConvertsFromString_AsSuccess()
    {
        FunctionResult result = "hello";

        Assert.False(result.IsError);
        Assert.Null(result.ErrorCode);
        Assert.Equal("hello", result.Text);
    }

    [Fact]
    public void FunctionResult_Error_RequiresACode()
    {
        _ = Assert.Throws<ArgumentException>(() => FunctionResult.Error("  ", "text"));
    }

    [Fact]
    public void FunctionResult_Default_IsAnErrorNotAnEmptySuccess()
    {
        // A struct's default is reachable without either factory, so it must not read as
        // "the operation succeeded and had nothing to say".
        FunctionResult result = default;

        Assert.True(result.IsError);
        Assert.Equal(FunctionResult.UninitializedErrorCode, result.ErrorCode);
        Assert.Contains("uninitialized", result.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FunctionResult_OkWithEmptyText_StaysASuccess()
    {
        // The default is distinguished by never having been assigned, not by being empty.
        var result = FunctionResult.Ok(string.Empty);

        Assert.False(result.IsError);
        Assert.Null(result.ErrorCode);
        Assert.Equal(string.Empty, result.Text);
    }

    [Fact]
    public async Task Handler_DefaultFunctionResult_IsDeliveredAsErrorNotAnEmptyBody()
    {
        var provider = new TypeFunctionProvider(new TestHandlerErrorSignalling());
        var function = provider.GetFunctions().First(f => f.Contract.Name == "never-assigned");

        var result = await function.Handler("{}", new ToolCallContext(), CancellationToken.None);

        var resolved = Assert.IsType<ToolHandlerResult.Resolved>(result);
        Assert.True(resolved.Payload.IsError);
        Assert.Equal(FunctionResult.UninitializedErrorCode, resolved.Payload.ErrorCode);
        // Previously this serialized to "{}" — an empty successful body.
        Assert.NotEqual("{}", resolved.Payload.Text);
    }

    [Fact]
    public void Contract_FunctionResultReturn_AdvertisesTheStringThatGoesOnTheWire()
    {
        var provider = new TypeFunctionProvider(new TestHandlerErrorSignalling());

        var signalled = provider.GetFunctions().First(f => f.Contract.Name == "signalled-error");
        var legacy = provider.GetFunctions().First(f => f.Contract.Name == "legacy-error");

        // FunctionRegistry renders ReturnType.Name into the system prompt, and only Text is
        // serialized — so an opted-in tool must describe itself exactly like a string one.
        Assert.Equal(typeof(string), signalled.Contract.ReturnType);
        Assert.Equal(legacy.Contract.ReturnType, signalled.Contract.ReturnType);
    }

    #endregion

    #region Test Classes

    public class TestHandlerErrorSignalling
    {
        [Function("legacy-error", "Returns an error-looking string without opting in")]
        public string LegacyError()
        {
            return "Error: not found.";
        }

        [Function("signalled-error", "Reports a failed operation with a code")]
        public FunctionResult SignalledError()
        {
            return FunctionResult.Error("thing_not_found", "Error: not found.");
        }

        [Function("signalled-ok", "Returns success through the opt-in type")]
        public FunctionResult SignalledOk()
        {
            return "all good";
        }

        /// <summary>
        ///     Stands in for every way a struct's default reaches a caller without passing
        ///     through a factory — an unassigned field, an array slot, a missing switch arm.
        /// </summary>
        [Function("never-assigned", "Returns the struct's default without going through a factory")]
        public FunctionResult NeverAssigned()
        {
            return default;
        }
    }

    public class TestHandlerBinding
    {
        [Function("bind-int", "Echoes an int parameter")]
        public string BindInt(int taskId)
        {
            return $"int:{taskId}";
        }

        [Function("bind-string", "Echoes a string parameter")]
        public string BindString(string taskId)
        {
            return $"string:{taskId}";
        }

        [Function("bind-defaulted", "Echoes parameters that declare C# defaults")]
        public string BindDefaulted(int limit = 7, bool flag = false)
        {
            return $"limit:{limit},flag:{flag}";
        }

        [Function("bind-required-flag", "Echoes a value-type parameter with no default")]
        public string BindRequiredFlag(bool enabled)
        {
            return $"required:{enabled}";
        }
    }

    public class TestHandlerWithFunctionAttribute
    {
        [Function("add", "Adds two numbers")]
        public int Add(int a, int b)
        {
            return a + b;
        }

        [Function("multiply")]
        [Description("Multiplies two numbers")]
        public static int Multiply([Description("First number")] int x, [Description("Second number")] int y)
        {
            return x * y;
        }

        [Function]
        public async Task<string> AsyncMethod(string input)
        {
            await Task.Delay(1);
            return $"Processed: {input}";
        }

        // Should not be included (no attribute)
        public static int Subtract(int a, int b)
        {
            return a - b;
        }
    }

    public class TestHandlerWithDescriptionAttribute
    {
        [Description("Concatenates two strings")]
        public string Concat(string a, string b)
        {
            return a + b;
        }

        [Description("Gets the length of a string")]
        public static int GetLength(string text)
        {
            return text?.Length ?? 0;
        }

        // Should not be included
        public static string NoAttribute(string input)
        {
            return input;
        }
    }

    public class TestHandlerMixed
    {
        private int _counter;

        [Function("calculate", "Performs calculation")]
        public double Calculate(double value, double factor = 2.0)
        {
            return value * factor;
        }

        [Description("Converts to uppercase")]
        public string ToUpper(string? text)
        {
            return text?.ToUpper() ?? string.Empty;
        }

        [Function("increment", "Increments and returns counter")]
        public int IncrementCounter()
        {
            return ++_counter;
        }
    }

    public class TestHandlerWithExceptions
    {
        [Function("divide", "Divides two numbers")]
        public double Divide(double a, double b)
        {
            return b == 0 ? throw new ArgumentException("Cannot divide by zero") : a / b;
        }

        [Function("asyncError")]
        public async Task<string> AsyncError()
        {
            await Task.Delay(1);
            throw new InvalidOperationException("Async error occurred");
        }
    }

    #endregion
}
