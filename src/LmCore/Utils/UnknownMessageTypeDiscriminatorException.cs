using System.Text.Json;

namespace AchieveAi.LmDotnetTools.LmCore.Utils;

/// <summary>
/// Thrown by <see cref="IMessageJsonConverter"/> when a persisted record carries a <c>$type</c>
/// discriminator this binary does not recognise.
/// </summary>
/// <remarks>
/// This is a SCHEMA fact, not a data fault: the bytes are well-formed and were almost certainly
/// written by a NEWER binary that knows a message type this one does not. Callers that degrade
/// per-record on a deserialization failure (see <c>MultiTurnAgentBase.RecoverAsync</c>) must be able
/// to tell it apart from bit rot, because during a rollback window it can silently drop a large
/// slice of history while every individual log line still reads like a damaged row.
/// It derives from <see cref="JsonException"/> so existing <c>catch (JsonException)</c> handlers keep
/// working unchanged; only handlers that WANT the distinction have to name this type.
/// </remarks>
public sealed class UnknownMessageTypeDiscriminatorException : JsonException
{
    public UnknownMessageTypeDiscriminatorException(string typeDiscriminator)
        : base($"Unknown type discriminator: {typeDiscriminator}")
    {
        TypeDiscriminator = typeDiscriminator;
    }

    /// <summary>The unrecognised <c>$type</c> value, verbatim.</summary>
    public string TypeDiscriminator { get; }
}
