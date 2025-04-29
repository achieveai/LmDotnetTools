using System.ComponentModel;
using ModelContextProtocol.Server;

namespace AchieveAi.LmDotnetTools.TestUtils.MockTools;

/// <summary>
/// Mock calculator tool for testing that mirrors CalculatorTool in Program.cs
/// </summary>
[McpServerToolType]
public static class MockCalculatorTool
{
    /// <summary>
    /// Adds two numbers
    /// </summary>
    [
        McpServerTool(
            Destructive = false,
            Idempotent = true,
            Name = "Add",
            OpenWorld = false,
            ReadOnly = true,
            Title = "Add numbers"),
        Description("Adds two numbers")
    ]
    public static double Add(double a, double b)
    {
        throw new NotImplementedException("Mock tool call - implement using callback override");
    }

    /// <summary>
    /// Subtracts the second number from the first
    /// </summary>
    [McpServerTool, Description("Subtracts the second number from the first")]
    public static double Subtract(double a, double b)
    {
        throw new NotImplementedException("Mock tool call - implement using callback override");
    }

    /// <summary>
    /// Multiplies two numbers
    /// </summary>
    [McpServerTool, Description("Multiplies two numbers")]
    public static double Multiply(double a, double b)
    {
        throw new NotImplementedException("Mock tool call - implement using callback override");
    }

    /// <summary>
    /// Divides the first number by the second
    /// </summary>
    [McpServerTool, Description("Divides the first number by the second")]
    public static double Divide(double a, double b)
    {
        throw new NotImplementedException("Mock tool call - implement using callback override");
    }

    /// <summary>
    /// Calculates the sine of an angle in degrees
    /// </summary>
    [McpServerTool, Description("Calculates the sine of an angle in degrees")]
    public static double Sin(double degrees)
    {
        throw new NotImplementedException("Mock tool call - implement using callback override");
    }

    /// <summary>
    /// Calculates the cosine of an angle in degrees
    /// </summary>
    [McpServerTool, Description("Calculates the cosine of an angle in degrees")]
    public static double Cos(double degrees)
    {
        throw new NotImplementedException("Mock tool call - implement using callback override");
    }

    /// <summary>
    /// Calculates the tangent of an angle in degrees
    /// </summary>
    [McpServerTool, Description("Calculates the tangent of an angle in degrees")]
    public static double Tan(double degrees)
    {
        throw new NotImplementedException("Mock tool call - implement using callback override");
    }

    /// <summary>
    /// Calculates the square root of a number
    /// </summary>
    [McpServerTool, Description("Calculates the square root of a number")]
    public static double Sqrt(double x)
    {
        throw new NotImplementedException("Mock tool call - implement using callback override");
    }

    /// <summary>
    /// Raises a number to a power
    /// </summary>
    [McpServerTool, Description("Raises a number to a power")]
    public static double Power(double x, double y)
    {
        throw new NotImplementedException("Mock tool call - implement using callback override");
    }
}