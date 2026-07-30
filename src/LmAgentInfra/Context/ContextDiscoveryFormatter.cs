using AchieveAi.LmDotnetTools.LmLifecycle;
using AchieveAi.LmDotnetTools.LmMultiTurn.Lifecycle;

namespace AchieveAi.LmDotnetTools.LmAgentInfra.Context;

/// <summary>
/// Formatter for sandbox-discovered context files (CLAUDE.md / AGENTS.md), used both at session
/// boot — appended to the system prompt for the initial workspace seed — and mid-session — emitted
/// as a user turn by <see cref="ContextDiscoveryInjector"/> when the gateway delivers a new file.
/// </summary>
/// <remarks>
/// <para>
/// The wrapper tag itself belongs to <see cref="RenderedContextBlock"/>, which also reads it back
/// out of an outgoing provider request. Keeping one definition for both directions is what makes
/// boot-time and mid-session rendering byte-identical — a context file injected after the first turn
/// looks the same to the model as one seeded at boot — and what stops a change to the tag from
/// silently making rendered context unrecognizable to the lifecycle seam that reports it.
/// </para>
/// <para>
/// The two <c>Build…</c> methods return the rendered string that callers have always concatenated
/// or wrapped; the <c>Render…</c> methods return the same text together with its provenance, for
/// callers that need to say where a block came from.
/// </para>
/// </remarks>
public sealed class ContextDiscoveryFormatter
{
    /// <summary>
    /// Builds the block appended to the system prompt at session boot. Returns an empty string
    /// when <paramref name="content"/> is null or empty so the caller can concatenate
    /// unconditionally.
    /// </summary>
    public string BuildSystemPromptBlock(string path, string? content, bool truncated) =>
        RenderSystemPromptBlock(path, content, truncated)?.Text ?? string.Empty;

    /// <summary>
    /// Builds the user-turn message body injected into a live conversation when the gateway
    /// discovers a new context file mid-session. Same wrapper tag as
    /// <see cref="BuildSystemPromptBlock"/> so the model's rendering of "what counts as a
    /// discovered file" stays consistent across boot and mid-session deliveries.
    /// </summary>
    public string BuildInjectedMessage(string path, string content, bool truncated) =>
        RenderInjectedMessage(path, content, truncated).Text;

    /// <summary>
    /// Renders the boot-time block and describes it, or <see langword="null"/> when there is nothing
    /// to render.
    /// </summary>
    /// <param name="path">Where the file came from, as the discovery reported it.</param>
    /// <param name="content">The file body; null or empty means no block.</param>
    /// <param name="truncated">Whether <paramref name="content"/> is shorter than the file.</param>
    public RenderedContextBlock? RenderSystemPromptBlock(string path, string? content, bool truncated) =>
        string.IsNullOrEmpty(content)
            ? null
            : RenderedContextBlock.Create(path, content, truncated, LifecycleContextPhases.Boot);

    /// <summary>
    /// Renders the mid-session block and describes it.
    /// </summary>
    /// <param name="path">Where the file came from, as the discovery reported it.</param>
    /// <param name="content">The file body.</param>
    /// <param name="truncated">Whether <paramref name="content"/> is shorter than the file.</param>
    /// <exception cref="ArgumentException"><paramref name="content"/> is null or empty.</exception>
    public RenderedContextBlock RenderInjectedMessage(string path, string content, bool truncated)
    {
        ArgumentException.ThrowIfNullOrEmpty(content);
        return RenderedContextBlock.Create(path, content, truncated, LifecycleContextPhases.MidSession);
    }
}
