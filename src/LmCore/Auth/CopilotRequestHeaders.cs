namespace AchieveAi.LmDotnetTools.LmCore.Auth;

/// <summary>
///     Produces the standard set of GitHub Copilot request headers (excluding <c>Authorization</c>)
///     shared by the HTTP <see cref="AchieveAi.LmDotnetTools.LmCore.Http.CopilotHeadersHandler"/> and
///     the WebSocket transport, so both decorate requests identically.
/// </summary>
public static class CopilotRequestHeaders
{
    /// <summary>
    ///     Enumerates the Copilot headers for a single request. <paramref name="interactionId"/> is
    ///     the per-request <c>x-interaction-id</c> the caller generates.
    /// </summary>
    public static IEnumerable<KeyValuePair<string, string>> Build(
        CopilotOptions options,
        CopilotSessionContext session,
        string interactionId
    )
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(session);

        yield return new KeyValuePair<string, string>("copilot-integration-id", options.IntegrationId);
        yield return new KeyValuePair<string, string>("editor-version", options.EditorVersion);
        yield return new KeyValuePair<string, string>("x-github-api-version", options.GitHubApiVersion);
        yield return new KeyValuePair<string, string>("User-Agent", options.UserAgent);
        yield return new KeyValuePair<string, string>("x-client-machine-id", session.MachineId);
        yield return new KeyValuePair<string, string>("x-client-session-id", session.ClientSessionId);
        yield return new KeyValuePair<string, string>("x-interaction-id", interactionId);
        yield return new KeyValuePair<string, string>("x-initiator", options.DefaultInitiator);

        if (!string.IsNullOrEmpty(options.DefaultInteractionType))
        {
            yield return new KeyValuePair<string, string>("x-interaction-type", options.DefaultInteractionType!);
        }

        if (options.ExtraHeaders is not null)
        {
            foreach (var header in options.ExtraHeaders)
            {
                yield return header;
            }
        }
    }
}
