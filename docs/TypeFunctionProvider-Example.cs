using System.ComponentModel;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Middleware;

// Example demonstrating the difference between AddFunctionsFromType and AddFunctionsFromObject

public class MathService
{
    private double _lastResult = 0;

    // Instance method - only registered with AddFunctionsFromObject
    [Function("addToMemory", "Adds a value to the stored memory")]
    public double AddToMemory(double value)
    {
        _lastResult += value;
        return _lastResult;
    }

    // Instance method - only registered with AddFunctionsFromObject
    [Description("Gets the current value in memory")]
    public double GetMemory()
    {
        return _lastResult;
    }

    // Static method - only registered with AddFunctionsFromType
    [Function("multiply", "Multiplies two numbers")]
    public static double Multiply(double a, double b)
    {
        return a * b;
    }

    // Static method - only registered with AddFunctionsFromType
    [Description("Calculates the square root of a number")]
    public static double SquareRoot(double value)
    {
        return Math.Sqrt(value);
    }
}

public class Example
{
    public static void Demo()
    {
        // Example 1: Register only static methods
        var registry1 = new FunctionRegistry();
        registry1.AddFunctionsFromType(typeof(MathService));
        
        // This will register:
        // - multiply (static)
        // - SquareRoot (static)
        // Will NOT register:
        // - addToMemory (instance)
        // - GetMemory (instance)
        
        var middleware1 = registry1.BuildMiddleware("StaticMathTools");

        // Example 2: Register only instance methods
        var mathService = new MathService();
        var registry2 = new FunctionRegistry();
        registry2.AddFunctionsFromObject(mathService);
        
        // This will register:
        // - addToMemory (instance)
        // - GetMemory (instance)
        // Will NOT register:
        // - multiply (static)
        // - SquareRoot (static)
        
        var middleware2 = registry2.BuildMiddleware("InstanceMathTools");

        // Example 3: Register both static and instance methods (from different registrations)
        var registry3 = new FunctionRegistry();
        
        // Add static methods from the type
        registry3.AddFunctionsFromType(typeof(MathService), "StaticMath", priority: 100);
        
        // Add instance methods from an object
        registry3.AddFunctionsFromObject(mathService, "InstanceMath", priority: 200);
        
        // This will register ALL methods:
        // - multiply (static)
        // - SquareRoot (static)  
        // - addToMemory (instance)
        // - GetMemory (instance)
        
        var middleware3 = registry3.BuildMiddleware("CompleteMathTools");

        // The instance methods maintain state between calls
        // Each call to addToMemory will accumulate the value
        // GetMemory will return the accumulated total
        
        // Static methods are stateless
        // Each call to multiply or SquareRoot is independent
    }
}