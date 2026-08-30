namespace LmStreaming.Sample.Services;

/// <summary>
/// The per-mode sub-agent prompt fragment (#610): a chat mode may declare a fragment that is folded
/// into EVERY sub-agent template's system prompt at catalog build time, so the mode sets
/// expectations for all sub-agents, not just the primary agent. This class owns the placement
/// vocabulary, its validation, and the fold itself.
/// </summary>
/// <remarks>
/// The fold happens at template-prompt level (each <c>SubAgentTemplate.SystemPrompt</c>) rather
/// than via <c>CharacteristicsAgentFactory</c>, because that factory is model-only
/// (<c>SubAgentCharacteristics</c> has no prompt slot) and is deliberately dropped on the
/// workflow-controller hop (<c>BuiltInSubAgentTemplates.CreateWorkflowControllerTemplates</c>
/// resets it) — a fragment carried there would silently vanish for controller delegates. The
/// template's <c>SystemPrompt</c> survives that hop, and the child prompt is read from the
/// template in exactly one place (<c>SubAgentManager</c>'s spawn path).
/// </remarks>
internal static class ModeSubAgentPrompt
{
    /// <summary>Placement value: fragment goes before the template's own prompt.</summary>
    public const string Prepend = "prepend";

    /// <summary>Placement value: fragment goes after the template's own prompt (the default).</summary>
    public const string Append = "append";

    /// <summary>
    /// True when <paramref name="placement"/> is a legal placement: absent (null) — which defaults
    /// to append when a fragment is present — or one of the two literal values. Anything else is
    /// refused at the boundary that received it (yaml load for system modes, 400 at the CRUD
    /// boundary for user modes) so an invalid value can never reach the fold.
    /// </summary>
    public static bool IsValidPlacement(string? placement) => placement is null || placement is Prepend or Append;

    /// <summary>
    /// Folds <paramref name="fragment"/> into <paramref name="templatePrompt"/> with a blank-line
    /// separator. Placement defaults to append for any value other than the literal
    /// <see cref="Prepend"/>; callers validate before storing, so the only non-append value that
    /// can arrive here is <see cref="Prepend"/>.
    /// </summary>
    public static string Fold(string templatePrompt, string fragment, string? placement) =>
        placement == Prepend ? $"{fragment}\n\n{templatePrompt}" : $"{templatePrompt}\n\n{fragment}";
}
