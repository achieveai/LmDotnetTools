using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmCore.Utils;

namespace McpServer.AspNetCore.Sample.Tools;

/// <summary>
/// Example weather tool that demonstrates IFunctionProvider implementation
/// </summary>
public class WeatherTool : IFunctionProvider
{
    public string ProviderName => "WeatherProvider";
    public int Priority => 100;

    public IEnumerable<FunctionDescriptor> GetFunctions()
    {
        var contract = new FunctionContract
        {
            Name = "get_weather",
            Description = "Get the current weather for a city",
            Parameters = new[]
            {
                new FunctionParameterContract
                {
                    Name = "city",
                    Description = "The city name",
                    ParameterType = SchemaHelper.CreateJsonSchemaFromType(typeof(string)),
                    IsRequired = true
                },
                new FunctionParameterContract
                {
                    Name = "unit",
                    Description = "Temperature unit (celsius or fahrenheit)",
                    ParameterType = SchemaHelper.CreateJsonSchemaFromType(typeof(string)),
                    IsRequired = false
                }
            },
            ReturnType = typeof(string)
        };

        yield return new FunctionDescriptor
        {
            Contract = contract,
            Handler = GetWeatherAsync,
            ProviderName = ProviderName
        };
    }

    private async Task<string> GetWeatherAsync(string argumentsJson)
    {
        await Task.Delay(100); // Simulate async operation

        var args = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(argumentsJson);
        var city = args?["city"].GetString() ?? "Unknown";
        var unit = args?.TryGetValue("unit", out var unitValue) == true
            ? unitValue.GetString() ?? "celsius"
            : "celsius";

        var temp = unit == "fahrenheit" ? 72 : 22;
        var conditions = new[] { "Sunny", "Cloudy", "Rainy", "Windy" };
        var condition = conditions[new Random().Next(conditions.Length)];

        var result = new
        {
            city,
            temperature = temp,
            unit,
            conditions = condition,
            humidity = 65
        };

        return JsonSerializer.Serialize(result);
    }
}

/// <summary>
/// Example calculator tool that demonstrates simple math operations
/// </summary>
public class CalculatorTool : IFunctionProvider
{
    public string ProviderName => "CalculatorProvider";
    public int Priority => 100;

    public IEnumerable<FunctionDescriptor> GetFunctions()
    {
        // Add operation
        yield return new FunctionDescriptor
        {
            Contract = new FunctionContract
            {
                Name = "add",
                ClassName = "Calculator",
                Description = "Add two numbers",
                Parameters = new[]
                {
                    new FunctionParameterContract
                    {
                        Name = "a",
                        Description = "First number",
                        ParameterType = SchemaHelper.CreateJsonSchemaFromType(typeof(double)),
                        IsRequired = true
                    },
                    new FunctionParameterContract
                    {
                        Name = "b",
                        Description = "Second number",
                        ParameterType = SchemaHelper.CreateJsonSchemaFromType(typeof(double)),
                        IsRequired = true
                    }
                },
                ReturnType = typeof(double)
            },
            Handler = async (args) =>
            {
                await Task.CompletedTask;
                var parsed = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(args);
                var a = parsed?["a"].GetDouble() ?? 0;
                var b = parsed?["b"].GetDouble() ?? 0;
                return JsonSerializer.Serialize(new { result = a + b });
            },
            ProviderName = ProviderName
        };

        // Multiply operation
        yield return new FunctionDescriptor
        {
            Contract = new FunctionContract
            {
                Name = "multiply",
                ClassName = "Calculator",
                Description = "Multiply two numbers",
                Parameters = new[]
                {
                    new FunctionParameterContract
                    {
                        Name = "a",
                        Description = "First number",
                        ParameterType = SchemaHelper.CreateJsonSchemaFromType(typeof(double)),
                        IsRequired = true
                    },
                    new FunctionParameterContract
                    {
                        Name = "b",
                        Description = "Second number",
                        ParameterType = SchemaHelper.CreateJsonSchemaFromType(typeof(double)),
                        IsRequired = true
                    }
                },
                ReturnType = typeof(double)
            },
            Handler = async (args) =>
            {
                await Task.CompletedTask;
                var parsed = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(args);
                var a = parsed?["a"].GetDouble() ?? 0;
                var b = parsed?["b"].GetDouble() ?? 0;
                return JsonSerializer.Serialize(new { result = a * b });
            },
            ProviderName = ProviderName
        };
    }
}

/// <summary>
/// Example file info tool that demonstrates file system operations
/// </summary>
public class FileInfoTool : IFunctionProvider
{
    public string ProviderName => "FileInfoProvider";
    public int Priority => 100;

    public IEnumerable<FunctionDescriptor> GetFunctions()
    {
        var contract = new FunctionContract
        {
            Name = "get_file_info",
            Description = "Get information about a file",
            Parameters = new[]
            {
                new FunctionParameterContract
                {
                    Name = "path",
                    Description = "The file path",
                    ParameterType = SchemaHelper.CreateJsonSchemaFromType(typeof(string)),
                    IsRequired = true
                }
            },
            ReturnType = typeof(string)
        };

        yield return new FunctionDescriptor
        {
            Contract = contract,
            Handler = GetFileInfoAsync,
            ProviderName = ProviderName
        };
    }

    private async Task<string> GetFileInfoAsync(string argumentsJson)
    {
        await Task.CompletedTask;

        var args = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(argumentsJson);
        var path = args?["path"].GetString();

        if (string.IsNullOrWhiteSpace(path))
        {
            return JsonSerializer.Serialize(new { error = "Path is required" });
        }

        if (!File.Exists(path))
        {
            return JsonSerializer.Serialize(new { error = $"File not found: {path}" });
        }

        var fileInfo = new FileInfo(path);
        var result = new
        {
            name = fileInfo.Name,
            fullPath = fileInfo.FullName,
            size = fileInfo.Length,
            created = fileInfo.CreationTimeUtc,
            modified = fileInfo.LastWriteTimeUtc,
            extension = fileInfo.Extension
        };

        return JsonSerializer.Serialize(result);
    }
}
