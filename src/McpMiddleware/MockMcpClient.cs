namespace AchieveAi.LmDotnetTools.McpMiddleware;

/// <summary>
/// Mock implementation of IMcpClient for testing
/// </summary>
public class MockMcpClient : IMcpClient
{
    private readonly string _id;
    private readonly string _name;

    /// <summary>
    /// Creates a new instance of the MockMcpClient
    /// </summary>
    /// <param name="id">ID of the client</param>
    /// <param name="name">Name of the client</param>
    public MockMcpClient(string id, string name)
    {
        _id = id;
        _name = name;
    }

    /// <summary>
    /// Lists the tools available from the MCP server
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of tools</returns>
    public Task<IList<McpClientTool>> ListToolsAsync(CancellationToken cancellationToken = default)
    {
        // Return a list of mock tools
        var tools = new List<McpClientTool>
        {
            new McpClientTool
            {
                Name = "hello",
                Description = "Says hello to the provided name",
                InputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        name = new
                        {
                            type = "string",
                            description = "The name to greet"
                        }
                    },
                    required = new[] { "name" }
                }
            },
            new McpClientTool
            {
                Name = "add",
                Description = "Adds two numbers",
                InputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        a = new
                        {
                            type = "number",
                            description = "First number"
                        },
                        b = new
                        {
                            type = "number",
                            description = "Second number"
                        }
                    },
                    required = new[] { "a", "b" }
                }
            }
        };

        return Task.FromResult<IList<McpClientTool>>(tools);
    }

    /// <summary>
    /// Calls a tool on the MCP server
    /// </summary>
    /// <param name="toolName">Name of the tool to call</param>
    /// <param name="arguments">Arguments for the tool</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Response from the tool</returns>
    public Task<CallToolResponse> CallToolAsync(
        string toolName,
        Dictionary<string, object?> arguments,
        CancellationToken cancellationToken = default)
    {
        // Handle different tools
        switch (toolName)
        {
            case "hello":
                if (arguments.TryGetValue("name", out var nameObj) && nameObj is string name)
                {
                    return Task.FromResult(new CallToolResponse
                    {
                        Content = new List<Content>
                        {
                            new Content
                            {
                                Type = "text",
                                Text = $"Hello, {name}! Welcome to the MCP server."
                            }
                        }
                    });
                }
                break;

            case "add":
                if (arguments.TryGetValue("a", out var aObj) && 
                    arguments.TryGetValue("b", out var bObj))
                {
                    // Try to convert to double
                    if (TryConvertToDouble(aObj, out var a) && 
                        TryConvertToDouble(bObj, out var b))
                    {
                        return Task.FromResult(new CallToolResponse
                        {
                            Content = new List<Content>
                            {
                                new Content
                                {
                                    Type = "text",
                                    Text = $"{a} + {b} = {a + b}"
                                }
                            }
                        });
                    }
                }
                break;
        }

        // Return an error if the tool or arguments are invalid
        return Task.FromResult(new CallToolResponse
        {
            Content = new List<Content>
            {
                new Content
                {
                    Type = "text",
                    Text = $"Error: Invalid tool or arguments"
                }
            }
        });
    }

    private bool TryConvertToDouble(object? value, out double result)
    {
        result = 0;

        if (value == null)
        {
            return false;
        }

        if (value is double d)
        {
            result = d;
            return true;
        }

        if (value is int i)
        {
            result = i;
            return true;
        }

        if (value is long l)
        {
            result = l;
            return true;
        }

        if (value is string s && double.TryParse(s, out var parsed))
        {
            result = parsed;
            return true;
        }

        return false;
    }
}
