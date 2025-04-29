using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace AchieveAi.LmDotnetTools.McpSampleServer;

class Program
{
    static async Task Main(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        builder.Logging.AddConsole(consoleLogOptions =>
        {
            // Configure all logs to go to stderr
            consoleLogOptions.LogToStandardErrorThreshold = LogLevel.Trace;
        });

        builder.Services
            .AddMcpServer()
            .WithStdioServerTransport()
            .WithToolsFromAssembly();

        await builder.Build().RunAsync();
    }
}

/// <summary>
/// Greeting tool that says hello to a name
/// </summary>
[McpServerToolType]
public static class GreetingTool
{
    /// <summary>
    /// Says hello to the provided name
    /// </summary>
    /// <param name="name">The name to greet</param>
    /// <returns>A greeting message</returns>
    [McpServerTool, Description("Greets a person by name")]
    public static string SayHello(string name)
    {
        return $"Hello, {name}! Nice to meet you.";
    }

    /// <summary>
    /// Says goodbye to the provided name
    /// </summary>
    /// <param name="name">The name to say goodbye to</param>
    /// <returns>A goodbye message</returns>
    [McpServerTool, Description("Says goodbye to a person by name")]
    public static string SayGoodbye(string name)
    {
        return $"Goodbye, {name}! Have a great day.";
    }
}

/// <summary>
/// Calculator tool for basic arithmetic operations
/// </summary>
[McpServerToolType]
public static class CalculatorTool
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
    public static double Add(double a, double b) => a + b;

    /// <summary>
    /// Subtracts the second number from the first
    /// </summary>
    [McpServerTool, Description("Subtracts the second number from the first")]
    public static double Subtract(double a, double b) => a - b;

    /// <summary>
    /// Multiplies two numbers
    /// </summary>
    [McpServerTool, Description("Multiplies two numbers")]
    public static double Multiply(double a, double b) => a * b;

    /// <summary>
    /// Divides the first number by the second
    /// </summary>
    [McpServerTool, Description("Divides the first number by the second")]
    public static double Divide(double a, double b)
    {
        if (b == 0)
        {
            throw new ArgumentException("Cannot divide by zero");
        }
        return a / b;
    }

    /// <summary>
    /// Calculates the sine of an angle in degrees
    /// </summary>
    [McpServerTool, Description("Calculates the sine of an angle in degrees")]
    public static double Sin(double degrees) => Math.Sin(DegreesToRadians(degrees));

    /// <summary>
    /// Calculates the cosine of an angle in degrees
    /// </summary>
    [McpServerTool, Description("Calculates the cosine of an angle in degrees")]
    public static double Cos(double degrees) => Math.Cos(DegreesToRadians(degrees));

    /// <summary>
    /// Calculates the tangent of an angle in degrees
    /// </summary>
    [McpServerTool, Description("Calculates the tangent of an angle in degrees")]
    public static double Tan(double degrees) => Math.Tan(DegreesToRadians(degrees));

    /// <summary>
    /// Calculates the square root of a number
    /// </summary>
    [McpServerTool, Description("Calculates the square root of a number")]
    public static double Sqrt(double x)
    {
        if (x < 0)
        {
            throw new ArgumentException("Cannot calculate square root of a negative number");
        }
        return Math.Sqrt(x);
    }

    /// <summary>
    /// Raises a number to a power
    /// </summary>
    [McpServerTool, Description("Raises a number to a power")]
    public static double Power(double x, double y) => Math.Pow(x, y);

    /// <summary>
    /// Converts degrees to radians
    /// </summary>
    private static double DegreesToRadians(double degrees) => degrees * Math.PI / 180.0;
}
