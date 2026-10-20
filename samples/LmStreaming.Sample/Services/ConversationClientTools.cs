using System.Text.Json;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;

namespace LmStreaming.Sample.Services;

/// <summary>
/// The conversation-scoped switch that turns the browser-hosted client tools (<c>AskUserQuestion</c> and
/// <c>NotifyClient</c>) off: the key it is stored under at provision, and the read that turns it back into a
/// value when the thread's agent is built. Kept together for the reason <see cref="ConversationSubAgentModel"/>
/// gives.
/// <para>
/// A headless caller such as Reviewd has no client attached. A question asked there parks the run until
/// someone answers it, and nobody will; a notification has nobody to show it to. Without the tools the
/// model cannot ask, so it has to finish the work and report what it could not decide.
/// </para>
/// </summary>
public static class ConversationClientTools
{
    /// <summary>
    /// Property key in a thread's <c>ThreadMetadata.Properties</c> holding
    /// <c>ProvisionConversationRequest.DisableClientTools</c>. Written only when true, and read back on every
    /// agent (re)creation for that thread, so the choice survives a restart and a mode or provider switch.
    /// </summary>
    public const string PropertyKey = "sample.clientToolsDisabled";

    /// <summary>
    /// Whether the conversation was provisioned with its client tools off. Absence, a missing thread or a
    /// value that is not a boolean all read as false, the behaviour every conversation had before; a store
    /// failure propagates, as <see cref="ConversationSubAgentModel.ReadAsync"/> does. Accepts the
    /// <see cref="JsonElement"/> the production store hands back as well as the in-memory
    /// <see cref="bool"/> (see <see cref="ThreadPropertyValue"/>).
    /// </summary>
    public static async Task<bool> ReadDisabledAsync(
        IConversationStore store,
        string threadId,
        CancellationToken ct = default
    )
    {
        ArgumentNullException.ThrowIfNull(store);

        if (string.IsNullOrWhiteSpace(threadId))
        {
            return false;
        }

        var metadata = await store.LoadMetadataAsync(threadId, ct).ConfigureAwait(false);
        if (metadata?.Properties is not { } properties || !properties.TryGetValue(PropertyKey, out var raw))
        {
            return false;
        }

        return raw switch
        {
            bool value => value,
            JsonElement { ValueKind: JsonValueKind.True } => true,
            _ => false,
        };
    }

    /// <summary>
    /// The same read over a listing row, whose properties arrive as strings: a stored <see cref="bool"/>
    /// <c>true</c> and a <see cref="JsonElement"/> <c>True</c> both project to <see cref="bool.TrueString"/>.
    /// Anything else, absence included, reads as false.
    /// </summary>
    public static bool IsDisabled(IReadOnlyDictionary<string, string?>? listingProperties) =>
        listingProperties?.TryGetValue(PropertyKey, out var raw) == true
        && string.Equals(raw, bool.TrueString, StringComparison.Ordinal);
}
