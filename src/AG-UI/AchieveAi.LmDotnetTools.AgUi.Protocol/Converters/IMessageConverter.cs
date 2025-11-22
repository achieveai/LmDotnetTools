using AchieveAi.LmDotnetTools.AgUi.DataObjects;
using AchieveAi.LmDotnetTools.LmCore.Messages;

namespace AchieveAi.LmDotnetTools.AgUi.Protocol.Converters;

/// <summary>
/// Converts LmCore messages to AG-UI protocol events
/// </summary>
public interface IMessageConverter
{
    /// <summary>
    /// Converts an LmCore message to zero or more AG-UI events
    /// </summary>
    /// <param name="message">The LmCore message to convert</param>
    /// <param name="sessionId">Session ID to associate with the events</param>
    /// <param name="threadId">Optional thread ID for conversation continuity</param>
    /// <param name="runId">Optional run ID for this specific execution</param>
    /// <returns>Enumerable of AG-UI events</returns>
    IEnumerable<AgUiEventBase> ConvertToAgUiEvents(IMessage message, string sessionId, string? threadId = null, string? runId = null);

    IEnumerable<AgUiEventBase> Flush(string sessionId, string? threadId = null, string? runId = null);
}
