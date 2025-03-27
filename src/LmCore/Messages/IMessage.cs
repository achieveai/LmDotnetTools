using System.Text.Json.Nodes;

namespace AchieveAi.LmDotnetTools.LmCore.Messages;

public interface IMessage
{
    public string? FromAgent { get; }

    public Role Role { get; }

    public JsonObject? Metadata { get; }
    
    public string? GenerationId { get; }
}