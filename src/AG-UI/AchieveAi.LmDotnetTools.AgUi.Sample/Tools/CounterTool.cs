using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmCore.Models;

namespace AchieveAi.LmDotnetTools.AgUi.Sample.Tools;

/// <summary>
///     Stateful counter tool demonstrating state management across calls
///     Maintains separate counters for different named counters
/// </summary>
public class CounterTool : IFunctionProvider
{
    private static readonly string[] sourceArray = ["increment", "decrement", "get", "reset"];
    private readonly Dictionary<string, int> _counters = [];
    private readonly object _lock = new();
    private readonly ILogger<CounterTool> _logger;

    public CounterTool(ILogger<CounterTool> logger)
    {
        _logger = logger;
        _logger.LogDebug("CounterTool initialized with empty counter state");
    }

    public string ProviderName => "CounterProvider";
    public int Priority => 100;

    public IEnumerable<FunctionDescriptor> GetFunctions()
    {
        var contract = new FunctionContract
        {
            Name = "counter",
            Description = "Manage named counters - increment, decrement, get value, or reset",
            Parameters =
            [
                new FunctionParameterContract
                {
                    Name = "operation",
                    ParameterType = new JsonSchemaObject { Type = "string", Enum = sourceArray },
                    Description = "The counter operation to perform",
                    IsRequired = true,
                },
                new FunctionParameterContract
                {
                    Name = "name",
                    ParameterType = new JsonSchemaObject { Type = "string" },
                    Description = "The name of the counter (default: 'default')",
                    IsRequired = false,
                },
                new FunctionParameterContract
                {
                    Name = "amount",
                    ParameterType = new JsonSchemaObject { Type = "integer" },
                    Description = "Amount to increment/decrement by (default: 1)",
                    IsRequired = false,
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
    ///     Executes the counter operation
    /// </summary>
    private async Task<string> ExecuteAsync(string arguments)
    {
        _logger.LogInformation("CounterTool called with arguments: {Args}", arguments);

        try
        {
            var args = JsonSerializer.Deserialize<CounterArgs>(arguments);
            if (args == null || string.IsNullOrWhiteSpace(args.Operation))
            {
                _logger.LogWarning("Invalid arguments provided to CounterTool");
                return JsonSerializer.Serialize(new { error = "Operation parameter is required" });
            }

            var name = args.Name ?? "default";
            var amount = args.Amount ?? 1;

            _logger.LogDebug(
                "Counter operation: {Operation} on counter '{Name}' with amount {Amount}",
                args.Operation,
                name,
                amount
            );

            // Simulate minimal delay
            await Task.Delay(10);

            int newValue;
            lock (_lock)
            {
                if (!_counters.ContainsKey(name))
                {
                    _counters[name] = 0;
                    _logger.LogDebug("Created new counter: {Name}", name);
                }

                newValue = args.Operation.ToLower() switch
                {
                    "increment" => _counters[name] += amount,
                    "decrement" => _counters[name] -= amount,
                    "get" => _counters[name],
                    "reset" => _counters[name] = 0,
                    _ => throw new ArgumentException($"Unknown operation: {args.Operation}"),
                };

                _counters[name] = newValue;
            }

            var result = new
            {
                operation = args.Operation,
                name,
                value = newValue,
                allCounters = new Dictionary<string, int>(_counters),
                timestamp = DateTime.UtcNow.ToString("o"),
            };

            var json = JsonSerializer.Serialize(result);
            _logger.LogInformation("CounterTool '{Name}' now at value: {Value}", name, newValue);

            return json;
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning("Invalid operation in CounterTool: {Error}", ex.Message);
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to parse arguments for CounterTool");
            return JsonSerializer.Serialize(new { error = "Invalid JSON format", details = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CounterTool execution failed");
            return JsonSerializer.Serialize(new { error = "Internal error", details = ex.Message });
        }
    }

    private record CounterArgs(string? Operation, string? Name, int? Amount);
}
