using System.Text.Json;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using LmStreaming.Sample.Models;

namespace LmStreaming.Sample.Services;

/// <summary>
/// The review round one conversation is authorized to publish to: the validated value, the key it is
/// stored under at provision, and the read that turns it back into a value when the thread's agent is
/// built.
/// </summary>
/// <remarks>
/// <para>
/// This scope is the ONLY thing that grants a conversation the typed publication tools
/// (<see cref="ReviewPublicationFunctionProvider"/>). It is written exclusively by an authenticated
/// service-to-service provision, never by the browser, and never by the model — so a chat cannot talk
/// its way into a publication surface, and a UI-created conversation has no route to one at all. The
/// gate is deliberately the scope rather than the mode id: a COPY of the <c>code-review-daemon</c> mode
/// is a different id, and gating on the id is exactly the class of bug <see cref="ModeCapabilities"/>
/// exists to have removed. It is also not a mode capability selection, because a selection is public
/// mode metadata anyone can copy, whereas a scope can only arrive with the S2S secret.
/// </para>
/// <para>
/// The field set mirrors the daemon's <c>PublicationScope</c> (CodeReviewDaemon.Sample
/// <c>Orchestration/IReviewPublicationOperations.cs</c>) MINUS the two per-action parts the agent
/// supplies — <c>actionId</c> and <c>sources</c>. Everything here is host-owned and unforgeable from a
/// tool argument. <see cref="LivePostingAuthorized"/> in particular is the daemon's collect-only gate:
/// if the model could set it, it could turn a dry run into a live write to the pull request.
/// </para>
/// <para>
/// Key and reader live in ONE file on purpose. The sibling knob
/// <see cref="SystemPromptAugmenter.AppendixPropertyKey"/> declared its key beside a helper no
/// production code called, so the value was written at provision and read by nothing for the whole
/// life of the field (#528/#529). Anything added here must be wired in the same change; the call site
/// is <c>Program.RegisterReviewPublicationTools</c>.
/// </para>
/// </remarks>
public sealed record ReviewPublicationScope
{
    /// <summary>
    /// Property key in a thread's <c>ThreadMetadata.Properties</c> holding the JSON form of
    /// <see cref="ProvisionConversationRequest.ReviewPublicationScope"/>. Written once at provision and
    /// read back on every agent (re)creation by <see cref="ReadAsync"/>, so the authorization survives a
    /// process restart and a provider switch exactly like the thread's workspace binding does.
    /// </summary>
    public const string PropertyKey = "sample.reviewPublicationScope";

    /// <summary>
    /// The engagement round every action in this conversation belongs to. A <see cref="long"/> because
    /// the daemon's route is <c>rounds/{roundId:long}</c> — the type IS the path-injection guard, so no
    /// character-level route sanitising is needed or possible to get wrong.
    /// </summary>
    public required long RoundId { get; init; }

    /// <summary>The PR provider (<c>github</c> / <c>ado</c>) the backend must publish through.</summary>
    public required string Provider { get; init; }

    /// <summary>The daemon's own repository row id.</summary>
    public required long RepoId { get; init; }

    /// <summary>The provider-native pull request identifier, as the daemon recorded it.</summary>
    public required string PrId { get; init; }

    /// <summary>The head the round was frozen against; the backend rejects an action that outlived it.</summary>
    public required string ExpectedHeadSha { get; init; }

    /// <summary>
    /// Whether the daemon authorized this round to write to the provider. False means the backend
    /// records every action as collected-only. Host-owned precisely because it is the live-write gate.
    /// </summary>
    public required bool LivePostingAuthorized { get; init; }

    /// <summary>
    /// Validates a provisioning caller's scope, returning <c>null</c> when any part of it is missing or
    /// unusable. Null means NO publication tools are registered — an incomplete scope must produce no
    /// surface at all rather than a surface that sends half-addressed requests.
    /// </summary>
    /// <remarks>
    /// <see cref="RoundId"/> and <see cref="RepoId"/> must be positive: the daemon's own
    /// <c>ValidateScope</c> rejects <c>RoundId &lt;= 0</c> as <c>invalid_scope</c>, so accepting one here
    /// would mint a surface whose every call is refused.
    /// </remarks>
    public static ReviewPublicationScope? TryCreate(ReviewPublicationScopeRequest? request)
    {
        if (
            request is null
            || request.RoundId <= 0
            || request.RepoId <= 0
            || string.IsNullOrWhiteSpace(request.Provider)
            || string.IsNullOrWhiteSpace(request.PrId)
            || string.IsNullOrWhiteSpace(request.ExpectedHeadSha)
        )
        {
            return null;
        }

        return new ReviewPublicationScope
        {
            RoundId = request.RoundId,
            Provider = request.Provider.Trim(),
            RepoId = request.RepoId,
            PrId = request.PrId.Trim(),
            ExpectedHeadSha = request.ExpectedHeadSha.Trim(),
            LivePostingAuthorized = request.LivePostingAuthorized,
        };
    }

    /// <summary>The value persisted under <see cref="PropertyKey"/>.</summary>
    public string ToPropertyValue() =>
        JsonSerializer.Serialize(
            new ReviewPublicationScopeRequest
            {
                RoundId = RoundId,
                Provider = Provider,
                RepoId = RepoId,
                PrId = PrId,
                ExpectedHeadSha = ExpectedHeadSha,
                LivePostingAuthorized = LivePostingAuthorized,
            }
        );

    /// <summary>
    /// Reads a persisted scope back, re-running the SAME validation the provisioning caller's value went
    /// through. Re-validating rather than trusting the blob is what makes a truncated, hand-edited or
    /// partially-written property fail closed instead of producing a half-addressed publication surface.
    /// </summary>
    public static ReviewPublicationScope? Parse(string? persisted)
    {
        if (string.IsNullOrWhiteSpace(persisted))
        {
            return null;
        }

        try
        {
            return TryCreate(JsonSerializer.Deserialize<ReviewPublicationScopeRequest>(persisted));
        }
        catch (JsonException)
        {
            // A property that is not the JSON this type writes means "no authorization", which is the
            // same answer as an absent one. Narrow on purpose: only the deserialize is guarded.
            return null;
        }
    }

    /// <summary>
    /// Reads the publication scope recorded for a thread at provision, or <c>null</c> when there is none
    /// — which is every conversation the UI creates and every conversation provisioned without one.
    /// <para>
    /// <b>Absence</b> never throws: a missing thread, a missing property, or a non-string value all mean
    /// "not authorized to publish". <b>Failure</b> does throw — a null <paramref name="store"/> is a
    /// wiring bug, and whatever the store's own <c>LoadMetadataAsync</c> raises propagates unchanged.
    /// This matches <see cref="ConversationSubAgentModel.ReadAsync"/>, the sibling reader on the same
    /// path; keep the two claims consistent.
    /// </para>
    /// <para>
    /// The value is extracted with <see cref="ThreadPropertyValue.AsString"/>, not a bare
    /// <c>raw is string</c> test: the production store round-trips the property bag through JSON, so a
    /// string written at provision is read back as a <see cref="JsonElement"/> and a plain type test
    /// returns null for every value that has actually been persisted.
    /// </para>
    /// </summary>
    public static async Task<ReviewPublicationScope?> ReadAsync(
        IConversationStore store,
        string threadId,
        CancellationToken ct = default
    )
    {
        ArgumentNullException.ThrowIfNull(store);

        if (string.IsNullOrWhiteSpace(threadId))
        {
            return null;
        }

        var metadata = await store.LoadMetadataAsync(threadId, ct).ConfigureAwait(false);
        if (metadata?.Properties is not { } properties || !properties.TryGetValue(PropertyKey, out var raw))
        {
            return null;
        }

        return Parse(ThreadPropertyValue.AsString(raw));
    }
}
