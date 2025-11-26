using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmCore.Models;

namespace AchieveAi.LmDotnetTools.AgUi.Sample.Tools;

/// <summary>
/// Basic calculator tool for mathematical operations
/// Demonstrates validation and error handling
/// </summary>
public class CalculatorTool : IFunctionProvider
{
    private readonly ILogger<CalculatorTool> _logger;

    public CalculatorTool(ILogger<CalculatorTool> logger)
    {
        _logger = logger;
        _logger.LogDebug("CalculatorTool initialized");
    }

    public string ProviderName => "CalculatorProvider";
    public int Priority => 100;

    private static readonly string[] sourceArray = ["add", "subtract", "multiply", "divide"];

    public IEnumerable<FunctionDescriptor> GetFunctions()
    {
        var contract = new FunctionContract
        {
            Name = "calculate",
            Description = "Perform basic mathematical calculations (add, subtract, multiply, divide)",
            Parameters =
            [
                new FunctionParameterContract
                {
                    Name = "operation",
                    ParameterType = new JsonSchemaObject { Type = "string", Enum = sourceArray },
                    Description = "The mathematical operation to perform",
                    IsRequired = true,
                },
                new FunctionParameterContract
                {
                    Name = "a",
                    ParameterType = new JsonSchemaObject { Type = "number" },
                    Description = "First number",
                    IsRequired = true,
                },
                new FunctionParameterContract
                {
                    Name = "b",
                    ParameterType = new JsonSchemaObject { Type = "number" },
                    Description = "Second number",
                    IsRequired = true,
                },
            ],
        };

        yield return new FunctionDescriptor
        {
            Contract = contract,
            Handler = ExecuteAsync,
            ProviderName = ProviderName,
        };
    }

    /// <summary>
    /// Executes the calculation
    /// </summary>
    private async Task<string> ExecuteAsync(string arguments)
    {
        _logger.LogInformation("CalculatorTool called with arguments: {Args}", arguments);

        try
        {
            var args = JsonSerializer.Deserialize<CalculatorArgs>(arguments);
            if (args == null)
            {
                _logger.LogWarning("Invalid arguments provided to CalculatorTool");
                return JsonSerializer.Serialize(new { error = "Invalid arguments" });
            }

            _logger.LogDebug("Performing calculation: {A} {Operation} {B}", args.A, args.Operation, args.B);

            // Simulate computation delay
            await Task.Delay(Random.Shared.Next(50, 150));

            var result = args.Operation?.ToLower() switch
            {
                "add" => args.A + args.B,
                "subtract" => args.A - args.B,
                "multiply" => args.A * args.B,
                "divide" => args.B != 0 ? args.A / args.B : throw new DivideByZeroException("Cannot divide by zero"),
                _ => throw new ArgumentException($"Unknown operation: {args.Operation}"),
            };

            var response = new
            {
                operation = args.Operation,
                a = args.A,
                b = args.B,
                result = result,
                timestamp = DateTime.UtcNow.ToString("o"),
            };

            var json = JsonSerializer.Serialize(response);
            _logger.LogInformation("CalculatorTool returning: {Result}", json);

            return json;
        }
        catch (DivideByZeroException ex)
        {
            _logger.LogWarning("Division by zero attempted in CalculatorTool");
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning("Invalid operation in CalculatorTool: {Error}", ex.Message);
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to parse arguments for CalculatorTool");
            return JsonSerializer.Serialize(new { error = "Invalid JSON format", details = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CalculatorTool execution failed");
            return JsonSerializer.Serialize(new { error = "Internal error", details = ex.Message });
        }
    }

    private record CalculatorArgs(string? Operation, double A, double B);
}
