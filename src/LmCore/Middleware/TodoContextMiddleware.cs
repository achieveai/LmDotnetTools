using System.Runtime.CompilerServices;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Messages;

namespace AchieveAi.LmDotnetTools.LmCore.Middleware;

/// <summary>
/// Middleware that automatically injects current todo context into conversations.
/// This helps maintain task awareness across tool interactions.
/// </summary>
public class TodoContextMiddleware : IStreamingMiddleware
{
  private readonly Func<string> _getTaskContext;

  /// <summary>
  /// Initializes a new instance of the <see cref="TodoContextMiddleware"/> class.
  /// </summary>
  /// <param name="getTaskContext">Function to get current task context</param>
  /// <param name="name">Optional name for the middleware</param>
  public TodoContextMiddleware(
      Func<string> getTaskContext,
      string? name = null)
  {
    _getTaskContext = getTaskContext ?? throw new ArgumentNullException(nameof(getTaskContext));
    Name = name ?? nameof(TodoContextMiddleware);
  }

  /// <summary>
  /// Gets the name of the middleware.
  /// </summary>
  public string? Name { get; }

  /// <summary>
  /// Invokes the middleware for synchronous scenarios.
  /// </summary>
  public async Task<IEnumerable<IMessage>> InvokeAsync(
      MiddlewareContext context,
      IAgent agent,
      CancellationToken cancellationToken = default)
  {
    var enrichedMessages = EnrichWithTodoContext(context.Messages);
    var enrichedContext = new MiddlewareContext(enrichedMessages, context.Options);
    return await agent.GenerateReplyAsync(enrichedContext.Messages, enrichedContext.Options, cancellationToken);
  }

  /// <summary>
  /// Invokes the middleware for streaming scenarios.
  /// </summary>
  public async Task<IAsyncEnumerable<IMessage>> InvokeStreamingAsync(
      MiddlewareContext context,
      IStreamingAgent agent,
      CancellationToken cancellationToken = default)
  {
    var enrichedMessages = EnrichWithTodoContext(context.Messages);
    var enrichedContext = new MiddlewareContext(enrichedMessages, context.Options);
    return await agent.GenerateReplyStreamingAsync(enrichedContext.Messages, enrichedContext.Options, cancellationToken);
  }

  private IEnumerable<IMessage> EnrichWithTodoContext(IEnumerable<IMessage> messages)
  {
    var messageList = messages.ToList();

    // Get current task context
    var currentContext = _getTaskContext();

    // Only add todo context if there are active tasks
    if (!string.IsNullOrWhiteSpace(currentContext))
    {
      // Add TodoContextMessage as the last system message
      var todoContextMessage = new TodoContextMessage
      {
        TodoContext = currentContext,
        FromAgent = "TodoContextMiddleware",
        Role = Role.System
      };

      messageList.Add(todoContextMessage);
    }

    return messageList;
  }
}
