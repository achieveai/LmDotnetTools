namespace AchieveAi.LmDotnetTools.LmLifecycle;

/// <summary>
/// Raised when data violates the lifecycle wire contract in a way forward compatibility cannot
/// absorb.
/// </summary>
/// <remarks>
/// This is deliberately narrow. Meeting an unknown event type, discriminator, or field is
/// <b>not</b> a contract violation — those are preserved and forwarded. This exception is for
/// structural failures: a missing required identifier, a non-positive ordinal, a malformed source
/// stream id, an unsupported protocol major, or a payload that cannot be encoded at all.
/// </remarks>
public sealed class LifecycleContractException : Exception
{
    /// <summary>Creates an exception with no message.</summary>
    public LifecycleContractException() { }

    /// <summary>Creates an exception describing the violation.</summary>
    /// <param name="message">What was structurally wrong.</param>
    public LifecycleContractException(string message)
        : base(message) { }

    /// <summary>Creates an exception describing the violation and its cause.</summary>
    /// <param name="message">What was structurally wrong.</param>
    /// <param name="innerException">The underlying failure.</param>
    public LifecycleContractException(string message, Exception innerException)
        : base(message, innerException) { }
}
