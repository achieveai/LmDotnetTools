namespace CodeReviewDaemon.Sample.Agents;

/// <summary>
/// One arm of a bounded, 2-way A/B review (plan §5). A variant varies along <b>both</b> axes the design
/// fixes: the LLM <see cref="ModelId"/> and the <see cref="SystemPrompt"/> (prompt/skill) — tools and
/// context are not required to differ. <see cref="CanWrite"/> is the capability axis: the primary
/// variant may post/push (it is the only arm that touches the provider or ReviewBot repo), while a
/// comparison (B) variant is collect-only — its output is persisted to SQLite and it is denied push/post
/// at the <see cref="Workspace.OperationPolicy"/> seam (mapped via its constructor's
/// <c>allowWriteOperations</c> parameter).
/// </summary>
internal sealed record ReviewVariant(string VariantId, string ModelId, string SystemPrompt, bool CanWrite);
