using System.ComponentModel;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using Xunit;

namespace AchieveAi.LmDotnetTools.LmCore.Tests.Middleware;

public class TypeFunctionProviderTests
{
    #region Test Classes
    
    public class TestHandlerWithFunctionAttribute
    {
        [Function("add", "Adds two numbers")]
        public int Add(int a, int b) => a + b;

        [Function("multiply")]
        [Description("Multiplies two numbers")]
        public static int Multiply(
            [Description("First number")] int x, 
            [Description("Second number")] int y) => x * y;

        [Function]
        public async Task<string> AsyncMethod(string input)
        {
            await Task.Delay(1);
            return $"Processed: {input}";
        }

        // Should not be included (no attribute)
        public int Subtract(int a, int b) => a - b;
    }

    public class TestHandlerWithDescriptionAttribute
    {
        [Description("Concatenates two strings")]
        public string Concat(string a, string b) => a + b;

        [Description("Gets the length of a string")]
        public static int GetLength(string text) => text?.Length ?? 0;

        // Should not be included
        public string NoAttribute(string input) => input;
    }

    public class TestHandlerMixed
    {
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

        private int _counter = 0;

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
            if (b == 0)
                throw new ArgumentException("Cannot divide by zero");
            return a / b;
        }

        [Function("asyncError")]
        public async Task<string> AsyncError()
        {
            await Task.Delay(1);
            throw new InvalidOperationException("Async error occurred");
        }
    }

    #endregion

    [Fact]
    public void TypeProvider_WithStaticType_ExtractsStaticMethodsOnly()
    {
        // Arrange
        var provider = new TypeFunctionProvider(typeof(TestHandlerWithFunctionAttribute));

        // Act
        var functions = provider.GetFunctions().ToList();

        // Assert
        Assert.Single(functions);
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
        Assert.Single(functions); // Only static GetLength
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
        Assert.Single(functions); // Only instance Concat
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
        var result = await addFunction.Handler(args);
        var resultValue = JsonSerializer.Deserialize<int>(result);

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
        var result = await multiplyFunction.Handler(args);
        var resultValue = JsonSerializer.Deserialize<int>(result);

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
        var result = await asyncFunction.Handler(args);
        var resultValue = JsonSerializer.Deserialize<string>(result);

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
        var result = await calculateFunction.Handler(args);
        var resultValue = JsonSerializer.Deserialize<double>(result);

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
        var result = await toUpperFunction.Handler(args);
        var resultValue = JsonSerializer.Deserialize<string>(result);

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
        var result1 = await incrementFunction.Handler("{}");
        var result2 = await incrementFunction.Handler("{}");
        var result3 = await incrementFunction.Handler("{}");

        // Assert
        Assert.Equal(1, JsonSerializer.Deserialize<int>(result1));
        Assert.Equal(2, JsonSerializer.Deserialize<int>(result2));
        Assert.Equal(3, JsonSerializer.Deserialize<int>(result3));
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
        var result = await divideFunction.Handler(args);
        var errorResult = JsonSerializer.Deserialize<Dictionary<string, string>>(result);

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
        var result = await asyncErrorFunction.Handler("{}");
        var errorResult = JsonSerializer.Deserialize<Dictionary<string, string>>(result);

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
        registry.AddFunctionsFromType(typeof(TestHandlerWithFunctionAttribute));
        var (contracts, handlers) = registry.Build();

        // Assert
        Assert.Single(contracts);
        Assert.Single(handlers);
        Assert.Contains(contracts, c => c.Name == "multiply");
    }

    [Fact]
    public void FunctionRegistryExtensions_AddFunctionsFromObject()
    {
        // Arrange
        var registry = new FunctionRegistry();
        var instance = new TestHandlerWithFunctionAttribute();

        // Act
        registry.AddFunctionsFromObject(instance);
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
        var types = new[] 
        { 
            typeof(TestHandlerWithFunctionAttribute),
            typeof(TestHandlerWithDescriptionAttribute) 
        };

        // Act
        registry.AddFunctionsFromTypes(types);
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
        
        registry.AddFunctionsFromObject(instance);
        var middleware = registry.BuildMiddleware("TestMiddleware");

        // Act - verify middleware was created with functions
        var (contracts, handlers) = registry.Build();
        
        // Assert - TestHandlerMixed has 3 instance methods (calculate, ToUpper, increment)
        Assert.NotNull(middleware);
        Assert.Equal(3, contracts.Count());
        
        // Test execution through handler
        var calculateHandler = handlers["calculate"];
        var result = await calculateHandler(JsonSerializer.Serialize(new { value = 5.0, factor = 3.0 }));
        Assert.Equal(15.0, JsonSerializer.Deserialize<double>(result));
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
}