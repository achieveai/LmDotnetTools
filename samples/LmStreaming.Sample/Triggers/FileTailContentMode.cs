namespace AchieveAi.LmDotnetTools.LmStreaming.Sample.Triggers;

/// <summary>
/// How much of a matched log line a <see cref="FileTailTriggerSource"/> forwards into the model's
/// context and the conversation's persisted history.
/// </summary>
public enum FileTailContentMode
{
    /// <summary>
    /// Forward the line with recognized credentials and PII replaced by <c>[redacted]</c>, then
    /// envelope-sanitized and length-capped. The default: it preserves the diagnostic value the
    /// wait was armed for while removing the secret shapes
    /// <see cref="TriggerContentRedactor"/> knows about.
    /// <para>
    /// A mitigation, not a guarantee — a secret in an unrecognized shape still gets through. Choose
    /// <see cref="MetadataOnly"/> where that residual risk is unacceptable.
    /// </para>
    /// </summary>
    Redacted = 0,

    /// <summary>
    /// Forward no file content at all — only the fact that a matching line arrived, and its size.
    /// The model learns the event happened and must read the file through an explicit tool call if
    /// it needs the text, which puts that disclosure under the host's normal tool policy instead of
    /// under a pattern list.
    /// </summary>
    MetadataOnly = 1,
}
