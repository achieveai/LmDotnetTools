using System.Text.Json;
using System.Text.Json.Serialization;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmCore.Models;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;

namespace LmStreaming.Sample.Services;

/// <summary>
///     Exposes <c>GetAgentTranscript</c> — the in-agent counterpart of
///     <c>GET /api/conversations/{threadId}/agents/{agentId}/transcript</c> — so an agent can read what
///     another agent in its hierarchy actually did rather than only what it summarised (#244).
/// </summary>
/// <remarks>
///     <para>
///         The provider is bound to ONE reader at construction. That is the whole security model: the
///         transcript is authorized for <see cref="AgentTranscriptToolProvider"/>'s own viewer, never for
///         an id the model typed. A host must therefore register it on the reader's own registry and keep
///         it out of sub-agent inheritance (<c>SubAgentOptions.NonInheritedToolNames</c>) — an inherited
///         instance would hand every descendant its parent's reach. A deeper agent gets its own
///         self-bound instance instead, via <c>SubAgentOptions.ChildToolProviderFactory</c>.
///     </para>
///     <para>
///         Access itself is not decided here. It comes from <see cref="AgentHierarchyService"/>, the same
///         call the HTTP route makes, so the tool and the API cannot answer differently for the same pair.
///     </para>
/// </remarks>
public sealed class AgentTranscriptToolProvider : IFunctionProvider
{
    /// <summary>The transcript read tool name.</summary>
    public const string GetAgentTranscriptToolName = "GetAgentTranscript";

    /// <summary>Every tool name this provider exposes; a host keeps these out of sub-agent inheritance.</summary>
    public static readonly IReadOnlyList<string> ToolNames = [GetAgentTranscriptToolName];

    /// <summary>How many of the most recent messages are returned when the caller does not say.</summary>
    private const int DefaultLimit = 40;

    /// <summary>Upper bound on a caller-supplied limit, so one read cannot swamp the reader's context.</summary>
    private const int MaxLimit = 200;

    /// <summary>Snake-case to match the argument names and the results of the sibling agent tools.</summary>
    private static readonly JsonSerializerOptions ResultJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly AgentHierarchyService _hierarchy;
    private readonly string _threadId;
    private readonly string _viewerAgentId;

    /// <summary>Creates the provider for one reader in one conversation.</summary>
    /// <param name="hierarchy">The shared hierarchy/authorization service the HTTP surface also uses.</param>
    /// <param name="threadId">The conversation whose hierarchy this reader belongs to.</param>
    /// <param name="viewerAgentId">
    ///     The collaboration id of the agent this instance reads AS. Never taken from tool arguments.
    /// </param>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public AgentTranscriptToolProvider(AgentHierarchyService hierarchy, string threadId, string viewerAgentId)
    {
        ArgumentNullException.ThrowIfNull(hierarchy);
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);
        ArgumentException.ThrowIfNullOrWhiteSpace(viewerAgentId);

        _hierarchy = hierarchy;
        _threadId = threadId;
        _viewerAgentId = viewerAgentId;
    }

    /// <inheritdoc />
    public string ProviderName => "AgentTranscriptTools";

    /// <summary>Low priority (high number) so parent tools take precedence on a name clash.</summary>
    public int Priority => 100;

    /// <inheritdoc />
    public IEnumerable<FunctionDescriptor> GetFunctions()
    {
        yield return new FunctionDescriptor
        {
            Contract = new FunctionContract
            {
                Name = GetAgentTranscriptToolName,
                Description =
                    "Read the transcript of another agent in this conversation's hierarchy — what it was "
                    + "asked, the tools it ran, and what it said — instead of relying on its summary. Use "
                    + "it when an agent's result is thin, surprising, or you need the evidence behind it. "
                    + "You can only read agents you are above (or yourself); the agent's private reasoning "
                    + "is never included. Use CheckAgents first if you need the list of agents and their "
                    + "status.",
                Parameters =
                [
                    new FunctionParameterContract
                    {
                        Name = "agent_id",
                        Description =
                            "The id of the agent whose transcript you want, as reported by Agent, "
                            + "CheckAgents, or GetAgents.",
                        ParameterType = new JsonSchemaObject { Type = new("string") },
                        IsRequired = true,
                    },
                    new FunctionParameterContract
                    {
                        Name = "limit",
                        Description =
                            $"Optional number of most recent messages to return (default {DefaultLimit}, "
                            + $"maximum {MaxLimit}).",
                        ParameterType = new JsonSchemaObject { Type = new("number") },
                        IsRequired = false,
                    },
                ],
            },
            Handler = HandleGetAgentTranscriptAsync,
            ProviderName = ProviderName,
        };
    }

    private async Task<ToolHandlerResult> HandleGetAgentTranscriptAsync(
        string argsJson,
        ToolCallContext context,
        CancellationToken cancellationToken
    )
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(argsJson);
        }
        catch (JsonException ex)
        {
            return ToolHandlerResult.FromError($"Tool arguments are not valid JSON: {ex.Message}", "invalid_args");
        }

        string? agentId;
        int limit;
        using (doc)
        {
            agentId = ReadString(doc.RootElement, "agent_id");
            limit = ReadLimit(doc.RootElement);
        }

        if (string.IsNullOrWhiteSpace(agentId))
        {
            return ToolHandlerResult.FromError("The 'agent_id' parameter is required.", "invalid_args");
        }

        var result = await _hierarchy
            .ReadTranscriptAsync(_threadId, agentId, _viewerAgentId, cancellationToken)
            .ConfigureAwait(false);

        return result.Outcome switch
        {
            AgentTranscriptOutcome.Allowed => ToolHandlerResult.FromText(Render(result, limit)),

            // A refusal names no agent and no thread: what the reader may not see includes whether it
            // exists. The stable reason code is enough for the model to stop asking.
            AgentTranscriptOutcome.Denied => ToolHandlerResult.FromError(
                "You cannot read that agent's transcript.",
                result.DenialCode
            ),

            // The hierarchy this tool reads is missing entirely (the conversation is gone, or
            // collaboration is off), which is a host-state problem rather than a refusal. Reported with
            // the SAME codes the HTTP route uses, so one denial vocabulary covers both surfaces.
            AgentTranscriptOutcome.UnknownThread => ToolHandlerResult.FromError(
                "This conversation's agent hierarchy is not available.",
                AgentTranscriptReasons.UnknownThread
            ),

            _ => ToolHandlerResult.FromError(
                "This conversation's agent hierarchy is not available.",
                AgentTranscriptReasons.CollaborationUnavailable
            ),
        };
    }

    /// <summary>
    ///     Renders an allowed transcript as the reader's-eye view: who the agent is, and the tail of what
    ///     it did. Only the most recent <paramref name="limit"/> messages are included — a transcript is
    ///     unbounded and this text lands in the caller's context window.
    /// </summary>
    private static string Render(AgentTranscriptResult result, int limit)
    {
        var agent = result.Agent!;
        var skipped = Math.Max(0, result.Messages.Count - limit);

        return JsonSerializer.Serialize(
            new
            {
                agent.AgentId,
                agent.Name,
                AgentType = agent.AgentType ?? agent.Template,
                agent.Status,
                agent.Role,
                MessageCount = result.Messages.Count,
                OmittedOlderMessages = skipped == 0 ? (int?)null : skipped,
                Messages = result.Messages.Skip(skipped).Select(RenderMessage).ToList(),
            },
            ResultJson
        );
    }

    /// <summary>
    ///     Projects one persisted row to the parts a reading agent can act on: the role, the text, and the
    ///     tool calls. A row that cannot be deserialized is reported as such rather than dropped, so a
    ///     gap in the evidence is visible instead of silent.
    /// </summary>
    private static object RenderMessage(PersistedMessage persisted)
    {
        var message = TranscriptProjection.TryDeserialize(persisted);
        if (message is null)
        {
            return new
            {
                persisted.Role,
                Type = persisted.MessageType,
                Unreadable = true,
            };
        }

        var toolCalls = (message as ICanGetToolCalls)
            ?.GetToolCalls()
            ?.Select(c => new { Name = c.FunctionName, Arguments = c.FunctionArgs })
            .ToList();

        return new
        {
            persisted.Role,
            Type = persisted.MessageType,
            Text = (message as ICanGetText)?.GetText(),
            ToolCalls = toolCalls is { Count: > 0 } ? toolCalls : null,
        };
    }

    private static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String ? prop.GetString() : null;

    /// <summary>
    ///     Reads the optional message limit. An absent, unusable, or out-of-range value falls back to the
    ///     default rather than failing the call: the caller wants the transcript, not an argument lecture.
    /// </summary>
    private static int ReadLimit(JsonElement root)
    {
        if (!root.TryGetProperty("limit", out var prop))
        {
            return DefaultLimit;
        }

        var requested = prop.ValueKind switch
        {
            JsonValueKind.Number when prop.TryGetInt32(out var value) => value,
            // Some models emit numbers as strings.
            JsonValueKind.String when int.TryParse(prop.GetString(), out var value) => value,
            _ => DefaultLimit,
        };

        return requested < 1 ? DefaultLimit : Math.Min(requested, MaxLimit);
    }
}
