using AchieveAi.LmDotnetTools.AnthropicProvider.Models;

namespace AchieveAi.LmDotnetTools.AnthropicProvider.Caching;

/// <summary>
///     Applies prompt caching breakpoints to an Anthropic request.
///     Anthropic allows up to 4 cache breakpoints per request. This strategy uses up to 3:
///     1. Last tool definition — tools rarely change between turns
///     2. System prompt — converted to array form with cache_control on last block
///     3. Last user message's last content block — marks end of conversation history
/// </summary>
public static class PromptCachingStrategy
{
    private static readonly AnthropicCacheControl s_fiveMinutes = new();
    private static readonly AnthropicCacheControl s_oneHour = new() { Ttl = "1h" };

    /// <summary>
    ///     Applies 5-minute cache breakpoints to the request and returns a new request with caching enabled.
    /// </summary>
    /// <param name="request">The request to mark.</param>
    public static AnthropicRequest Apply(AnthropicRequest request) => Apply(request, oneHour: false);

    /// <summary>
    ///     Applies cache breakpoints to the request and returns a new request with caching enabled.
    /// </summary>
    /// <param name="request">The request to mark.</param>
    /// <param name="oneHour">
    ///     Marks every breakpoint for 1 hour. All of them, because reading a 5-minute entry only restarts its
    ///     5 minutes, so a 5-minute tail behind 1-hour markers would never become a 1-hour entry.
    /// </param>
    public static AnthropicRequest Apply(AnthropicRequest request, bool oneHour)
    {
        ArgumentNullException.ThrowIfNull(request);
        var result = request;
        var marker = oneHour ? s_oneHour : s_fiveMinutes;

        // 1. Mark last tool definition with cache_control
        result = ApplyToolsCaching(result, marker);

        // 2. Convert system prompt to array form and mark with cache_control
        result = ApplySystemCaching(result, marker);

        // 3. Mark last user message's last content block with cache_control
        result = ApplyMessagesCaching(result, marker);

        return result;
    }

    private static AnthropicRequest ApplyToolsCaching(AnthropicRequest request, AnthropicCacheControl marker)
    {
        if (request.Tools is not { Count: > 0 })
        {
            return request;
        }

        // Clone the tools list and mark the last tool with cache_control
        var tools = new List<object>(request.Tools);
        var lastTool = tools[^1];

        tools[^1] = lastTool switch
        {
            AnthropicTool tool => tool with { CacheControl = marker },
            AnthropicBuiltInTool builtIn => builtIn with { CacheControl = marker },
            _ => lastTool,
        };

        return request with
        {
            Tools = tools,
        };
    }

    private static AnthropicRequest ApplySystemCaching(AnthropicRequest request, AnthropicCacheControl marker)
    {
        // If already using SystemContent array form, mark the last block
        if (request.SystemContent is { Count: > 0 })
        {
            var systemContent = new List<AnthropicSystemContent>(request.SystemContent);
            systemContent[^1] = systemContent[^1] with { CacheControl = marker };
            return request with { SystemContent = systemContent };
        }

        // Convert string system prompt to array form with cache_control
        if (!string.IsNullOrEmpty(request.System))
        {
            var systemContent = new List<AnthropicSystemContent>
            {
                new()
                {
                    Type = "text",
                    Text = request.System,
                    CacheControl = marker,
                },
            };

            return request with
            {
                System = null,
                SystemContent = systemContent,
            };
        }

        return request;
    }

    private static AnthropicRequest ApplyMessagesCaching(AnthropicRequest request, AnthropicCacheControl marker)
    {
        if (request.Messages is not { Count: > 0 })
        {
            return request;
        }

        // Find the last user message and mark its last content block
        for (var i = request.Messages.Count - 1; i >= 0; i--)
        {
            var message = request.Messages[i];
            if (message.Role == "user" && message.Content.Count > 0)
            {
                var lastContent = message.Content[^1];
                message.Content[^1] = lastContent with { CacheControl = marker };
                return request;
            }
        }

        return request;
    }
}
