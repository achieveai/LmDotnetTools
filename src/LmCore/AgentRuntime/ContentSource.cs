namespace AchieveAi.LmDotnetTools.LmCore.AgentRuntime;

/// <summary>
///     Closed discriminated union describing how a profile-provided content fragment
///     (skill body, sub-agent definition, etc.) is supplied to the SDK.
/// </summary>
/// <remarks>
///     Construction is pure: no I/O happens here. Provider materializers own all file
///     reads and writes when projecting a profile into a CLI staging directory.
/// </remarks>
public abstract record ContentSource
{
    private ContentSource() { }

    /// <summary>
    ///     Content is read from <see cref="Value"/> on the local filesystem. If the path
    ///     points at a directory, the materializer copies its contents recursively.
    ///     If it points at a file, the materializer copies the single file.
    /// </summary>
    public sealed record FromPath(string Value) : ContentSource;

    /// <summary>
    ///     Content is supplied inline as a literal markdown string. The materializer
    ///     writes it verbatim to the canonical location for the consuming provider.
    /// </summary>
    public sealed record FromInline(string Content) : ContentSource;
}
