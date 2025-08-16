# TypeFunctionProvider Usage Guide

The `TypeFunctionProvider` and related extension methods allow you to easily register .NET types and objects as functions for use with `FunctionCallMiddleware`. This provides a simple way to expose your existing code as AI-callable functions without the MCP overhead.

## Features

- **Attribute-based Registration**: Use `FunctionAttribute` or `DescriptionAttribute` to mark methods as functions
- **Automatic Marshaling**: Handles JSON serialization/deserialization of parameters and return values
- **Support for Async Methods**: Works with both synchronous and `Task`-based async methods
- **Parameter Metadata**: Extracts parameter descriptions and requirements from attributes
- **Instance State Management**: Can work with stateful objects or static methods
- **Error Handling**: Gracefully handles and reports exceptions

## Quick Start

### 1. Mark Your Methods with Attributes

```csharp
using System.ComponentModel;
using AchieveAi.LmDotnetTools.LmCore.Agents;

public class CalculatorService
{
    [Function("add", "Adds two numbers together")]
    public int Add(int a, int b) => a + b;

    [Description("Multiplies two numbers")]
    public static double Multiply(
        [Description("First number")] double x, 
        [Description("Second number")] double y) => x * y;

    [Function("divide")]
    public async Task<double> DivideAsync(double numerator, double denominator)
    {
        if (denominator == 0)
            throw new ArgumentException("Cannot divide by zero");
        
        await Task.Delay(1); // Simulate async work
        return numerator / denominator;
    }
}
```

### 2. Register with FunctionRegistry

#### Register an Instance (instance methods only)

```csharp
using AchieveAi.LmDotnetTools.LmCore.Middleware;

var calculator = new CalculatorService();
var registry = new FunctionRegistry();

// Register all marked instance methods from the object
// Note: Static methods are NOT included when registering an object
registry.AddFunctionsFromObject(calculator);

// Build the middleware
var middleware = registry.BuildMiddleware("CalculatorMiddleware");
```

#### Register a Type (static methods only)

```csharp
var registry = new FunctionRegistry();

// Register only static methods from the type
registry.AddFunctionsFromType(typeof(CalculatorService));

var middleware = registry.BuildMiddleware();
```

#### Register Multiple Types

```csharp
var registry = new FunctionRegistry();

var types = new[] 
{ 
    typeof(CalculatorService),
    typeof(StringService),
    typeof(DataService)
};

registry.AddFunctionsFromTypes(types);
```

#### Register from Assembly

```csharp
var registry = new FunctionRegistry();

// Scans assembly for all types with marked methods
registry.AddFunctionsFromAssembly(Assembly.GetExecutingAssembly());
```

### 3. Use with an Agent

```csharp
// Create your agent with the middleware
var agent = new MyAgent()
    .UseMiddleware(middleware);

// The agent can now call your functions
var response = await agent.GenerateReplyAsync(messages);
```

## Method Marking Rules

Methods are included if they have:
- `FunctionAttribute` - Allows custom function name and description
- `DescriptionAttribute` - Uses method name as function name

Methods are excluded if they are:
- Special methods (constructors, property getters/setters)
- Compiler-generated methods
- Methods without any marking attributes

## Parameter Handling

### Required vs Optional Parameters

Parameters are considered **required** unless:
- They have a default value
- They are nullable reference types (e.g., `string?`)
- They are `Nullable<T>` value types (e.g., `int?`)

```csharp
public class ParameterExamples
{
    [Function]
    public void Example(
        int required,              // Required
        int optional = 10,         // Optional (has default)
        string? nullable = null,   // Optional (nullable)
        int? nullableInt = null)   // Optional (nullable value type)
    {
        // Implementation
    }
}
```

### Parameter Descriptions

Use `DescriptionAttribute` on parameters to provide documentation:

```csharp
[Function("search", "Searches for items")]
public List<Item> Search(
    [Description("The search query text")] string query,
    [Description("Maximum number of results to return")] int limit = 10)
{
    // Implementation
}
```

## Error Handling

Exceptions thrown by methods are caught and returned as error responses:

```csharp
// If a method throws an exception:
throw new InvalidOperationException("Something went wrong");

// The middleware returns:
{
    "error": "Something went wrong",
    "type": "InvalidOperationException"
}
```

## Stateful Services

The provider maintains instance state between calls:

```csharp
public class CounterService
{
    private int _count = 0;

    [Function("increment", "Increments and returns the counter")]
    public int Increment() => ++_count;

    [Function("getCount", "Gets the current count")]
    public int GetCount() => _count;
}

// Register instance
var counter = new CounterService();
registry.AddFunctionsFromObject(counter);

// Each call maintains state
// First call to increment returns 1
// Second call to increment returns 2
// Call to getCount returns 2
```

## Integration with Existing FunctionRegistry Features

The `TypeFunctionProvider` integrates seamlessly with other providers:

```csharp
var registry = new FunctionRegistry();

// Add functions from your types
registry.AddFunctionsFromObject(myService);

// Add MCP functions
registry.AddProvider(new McpFunctionProvider());

// Add individual functions
registry.AddFunction(customContract, customHandler);

// Set conflict resolution
registry.WithConflictResolution(ConflictResolution.TakeFirst);

// Build middleware with all functions
var middleware = registry.BuildMiddleware();
```

## Complete Example

```csharp
using System.ComponentModel;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Middleware;

// Define your service
public class WeatherService
{
    [Function("getWeather", "Gets weather for a city")]
    public async Task<WeatherInfo> GetWeatherAsync(
        [Description("City name")] string city,
        [Description("Include forecast")] bool includeForecast = false)
    {
        // Implementation
        await Task.Delay(100);
        return new WeatherInfo 
        { 
            City = city, 
            Temperature = 72, 
            Condition = "Sunny" 
        };
    }

    [Description("Converts temperature between units")]
    public static double ConvertTemperature(
        double value, 
        string fromUnit = "F", 
        string toUnit = "C")
    {
        if (fromUnit == "F" && toUnit == "C")
            return (value - 32) * 5 / 9;
        if (fromUnit == "C" && toUnit == "F")
            return value * 9 / 5 + 32;
        return value;
    }
}

// Register and use
var weatherService = new WeatherService();
var registry = new FunctionRegistry()
    .AddFunctionsFromObject(weatherService);

var middleware = registry.BuildMiddleware("WeatherTools");

// Use with your agent
var agent = new YourAgent()
    .UseMiddleware(middleware);
```

## Benefits Over MCP

While MCP provides a standardized protocol for tool communication, `TypeFunctionProvider` offers:

1. **Simpler Setup**: No need for MCP attributes or server setup
2. **Direct Integration**: Functions run in-process without IPC overhead
3. **Flexible Marking**: Use standard .NET attributes
4. **Instance Support**: Work with stateful objects naturally
5. **Less Boilerplate**: Minimal code changes to existing services

Choose `TypeFunctionProvider` when you want to quickly expose existing .NET code as AI functions without the complexity of MCP.