using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmCore.Models;

namespace LmStreaming.Sample.Services;

/// <summary>
/// The six typed publication operations of spec §6.2, exposed to the PARENT review conversation only.
/// </summary>
/// <remarks>
/// <para>
/// The provider is bound to ONE <see cref="IReviewPublicationBridge"/>, and that bridge is bound to one
/// round-scoped pull request. That is the whole security model: the scope is host-owned and arrives
/// only through an authenticated service-to-service provision, never from a tool argument. A host must
/// therefore register this on the parent conversation's own registry AND keep
/// <see cref="ToolNames"/> out of sub-agent inheritance
/// (<c>SubAgentOptions.NonInheritedToolNames</c>) — an inherited instance would hand every context
/// gatherer, specialist and workflow delegate the ability to write to the pull request under the
/// daemon's credentials. Unlike <see cref="AgentTranscriptToolProvider"/> there is no per-child rebind:
/// publication is the parent's decision to make, and a child that needs something published reports it
/// upward. <c>Program.RegisterReviewPublicationTools</c> performs both halves as one step.
/// </para>
/// <para>
/// Parameter names are camelCase and match the daemon's request records
/// (<c>RootSummaryRequest</c>, <c>SummaryDeltaRequest</c>, <c>InlineFindingsRequest</c>,
/// <c>ClarificationQuestionRequest</c>, <c>DiscussionReplyRequest</c>, <c>FinalizeRoundRequest</c>),
/// which the controller binds with <see cref="JsonSerializerDefaults.Web"/>. Keeping the names identical
/// is what lets the bridge forward the agent's values verbatim instead of translating them — a
/// translation layer is where wording quietly stops being the agent's own.
/// </para>
/// <para>
/// This side chooses nothing about the pull request. It carries the agent's wording, grouping and
/// typed intent to the backend, and hands the backend's typed receipt or typed rejection straight back
/// — never substituting a host-authored summary for a failed action (spec §6.1).
/// </para>
/// </remarks>
public sealed class ReviewPublicationFunctionProvider : IFunctionProvider
{
    /// <summary>Error code for a call the host refuses before it can reach the backend.</summary>
    private const string InvalidArgumentsCode = "invalid_args";

    private readonly IReviewPublicationBridge _bridge;

    /// <summary>Creates the provider for one round-scoped parent conversation.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="bridge" /> is null.</exception>
    public ReviewPublicationFunctionProvider(IReviewPublicationBridge bridge)
    {
        ArgumentNullException.ThrowIfNull(bridge);
        _bridge = bridge;
    }

    /// <inheritdoc />
    public string ProviderName => "ReviewPublicationTools";

    /// <summary>Low priority (high number) so a host tool of the same name would take precedence.</summary>
    public int Priority => 100;

    /// <inheritdoc />
    public IEnumerable<FunctionDescriptor> GetFunctions()
    {
        foreach (var operation in Operations)
        {
            yield return new FunctionDescriptor
            {
                Contract = operation.Contract,
                Handler = (args, _, ct) => HandleAsync(operation, args, ct),
                ProviderName = ProviderName,
            };
        }
    }

    /// <summary>
    /// Validates the agent's arguments mechanically, forwards them, and renders the answer. Validation
    /// here is deliberately shallow — presence and shape only. Whether a round is running, an action id
    /// already has a receipt, a source reference is known, an anchor is in the diff, or the head still
    /// matches is the backend's question, and answering it twice in two places is how the two answers
    /// start to disagree.
    /// </summary>
    private async Task<ToolHandlerResult> HandleAsync(
        PublicationOperation operation,
        string argsJson,
        CancellationToken cancellationToken
    )
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(argsJson);
        }
        catch (JsonException ex)
        {
            return ToolHandlerResult.FromError(
                $"Tool arguments are not valid JSON: {ex.Message}",
                InvalidArgumentsCode
            );
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return ToolHandlerResult.FromError("Tool arguments must be a JSON object.", InvalidArgumentsCode);
            }

            if (FirstMissingField(operation, root) is { } missing)
            {
                return ToolHandlerResult.FromError(
                    $"The '{missing}' parameter is required for {operation.Name}. Nothing was published.",
                    InvalidArgumentsCode
                );
            }

            var result = await _bridge.SendAsync(operation.Name, root, cancellationToken).ConfigureAwait(false);

            return result.Accepted
                ? ToolHandlerResult.FromText(result.Payload)
                : ToolHandlerResult.FromError(result.Payload, result.RejectionCode);
        }
    }

    /// <summary>
    /// The first required field that is absent or empty, or null when the call is well-formed. An empty
    /// string, an empty array and an absent key are the same answer: there is nothing to publish.
    /// </summary>
    private static string? FirstMissingField(PublicationOperation operation, JsonElement root)
    {
        foreach (var field in operation.RequiredFields)
        {
            if (!root.TryGetProperty(field.Name, out var value) || !field.Accepts(value.ValueKind))
            {
                return field.Name;
            }

            var isEmpty = value.ValueKind switch
            {
                JsonValueKind.String => string.IsNullOrWhiteSpace(value.GetString()),
                JsonValueKind.Array => value.GetArrayLength() == 0,
                _ => false,
            };

            if (isEmpty)
            {
                return field.Name;
            }
        }

        return null;
    }

    #region Operation table

    /// <summary>
    /// One required argument: its name and the JSON kind it must arrive as. <see cref="JsonValueKind.True"/>
    /// stands for "a boolean" — JSON booleans surface as two distinct kinds, and a required <c>false</c>
    /// is every bit as much an answer as a required <c>true</c>.
    /// </summary>
    private sealed record RequiredField(string Name, JsonValueKind Kind)
    {
        public bool Accepts(JsonValueKind actual) =>
            Kind == JsonValueKind.True ? actual is JsonValueKind.True or JsonValueKind.False : actual == Kind;
    }

    /// <summary>One publication operation: its contract and the arguments the host insists on.</summary>
    private sealed record PublicationOperation(
        string Name,
        FunctionContract Contract,
        IReadOnlyList<RequiredField> RequiredFields
    );

    /// <summary>
    /// Every action carries the stable id the daemon supplied in the publication-context block. It is
    /// the idempotency key: repeating an accepted action with the same id returns the prior receipt
    /// instead of publishing twice (spec §6.5), which is why the agent must reuse it rather than mint a
    /// new one on retry. The bridge lifts it into the request's <c>scope.actionId</c>.
    /// </summary>
    private static FunctionParameterContract ActionIdParameter() =>
        new()
        {
            Name = "actionId",
            Description =
                "The stable action id supplied for this action in the publication context. Reuse it "
                + "exactly when retrying; never invent a new one for an action that already has a receipt.",
            ParameterType = JsonSchemaObject.String("Stable action id from the publication context."),
            IsRequired = true,
        };

    /// <summary>
    /// The evidence this action rests on. REQUIRED and non-empty: the daemon's
    /// <c>ReviewStore.ValidateSourceReferences</c> rejects an empty set outright ("Structured evidence
    /// requires at least one exact source reference"), so an action with no citations cannot publish.
    /// The host checks only the shape — that there is at least one entry — and leaves whether a citation
    /// is KNOWN to the backend, which answers it against the engagement and rejects a stranger as
    /// <c>invalid_source_reference</c>. Checking emptiness here turns an opaque backend code into a
    /// message the agent can act on; checking existence here would be the same question answered twice.
    /// The bridge lifts the value into the request's <c>scope.sources</c>.
    /// </summary>
    private static FunctionParameterContract SourcesParameter() =>
        new()
        {
            Name = "sources",
            Description =
                "The context records backing this action — at least one, each with the exact content "
                + "hash you were given. Cite what you actually relied on; the backend rejects an empty "
                + "set, a repeated record, and any reference it cannot match in this engagement.",
            ParameterType = JsonSchemaObject.Array(
                new JsonSchemaObject
                {
                    Type = new("object"),
                    Required = ["sourceRecordId", "contentSha256"],
                    Properties = new Dictionary<string, JsonSchemaObject>
                    {
                        ["sourceRecordId"] = JsonSchemaObject.String("The supplied source record id."),
                        ["contentSha256"] = JsonSchemaObject.String("That record's supplied content hash."),
                    },
                },
                "One entry per source relied on. Must not be empty."
            ),
            IsRequired = true,
        };

    /// <summary>The arguments every operation requires, before its own operation-specific ones.</summary>
    private static readonly RequiredField[] CommonRequiredFields =
    [
        new("actionId", JsonValueKind.String),
        new("sources", JsonValueKind.Array),
    ];

    private static FunctionParameterContract BodyParameter(string description) =>
        new()
        {
            Name = "body",
            Description = description,
            ParameterType = JsonSchemaObject.String(description),
            IsRequired = true,
        };

    /// <summary>
    /// The provider-native discussion to continue, as an opaque id taken from the supplied publication
    /// context. Deliberately a bare string, not a constructed ref: the daemon resolves provider thread
    /// identity itself, and an id the agent assembled is an id it guessed.
    /// </summary>
    private static FunctionParameterContract ProviderTargetIdParameter(string description, bool isRequired) =>
        new()
        {
            Name = "providerTargetId",
            Description = description,
            ParameterType = JsonSchemaObject.String(description),
            IsRequired = isRequired,
        };

    /// <summary>
    /// The six operations, defined once. The descriptors, the required-argument checks and
    /// <see cref="ToolNames"/> all read from this table, so adding or renaming an operation cannot leave
    /// one of the three behind.
    /// </summary>
    private static readonly IReadOnlyList<PublicationOperation> Operations =
    [
        new(
            "CreateRootSummary",
            new FunctionContract
            {
                Name = "CreateRootSummary",
                Description =
                    "Create this pull request's ONE root review summary, at PR level. Use it for the "
                    + "first material review only; every later round appends with AppendSummaryDelta "
                    + "instead. Your wording is published unchanged.",
                Parameters =
                [
                    ActionIdParameter(),
                    BodyParameter("The complete root summary, authored by you and published verbatim."),
                    SourcesParameter(),
                ],
            },
            [.. CommonRequiredFields, new("body", JsonValueKind.String)]
        ),
        new(
            "AppendSummaryDelta",
            new FunctionContract
            {
                Name = "AppendSummaryDelta",
                Description =
                    "Append this round's delta to the existing root summary: what was resolved, what is "
                    + "new, which questions were answered or remain open, which conclusions changed, and "
                    + "what is still uncertain. History is never replaced and no second summary root is "
                    + "created.",
                Parameters =
                [
                    ActionIdParameter(),
                    BodyParameter("This round's delta only — do not restate the whole summary."),
                    SourcesParameter(),
                ],
            },
            [.. CommonRequiredFields, new("body", JsonValueKind.String)]
        ),
        new(
            "SubmitInlineFindings",
            new FunctionContract
            {
                Name = "SubmitInlineFindings",
                Description =
                    "Submit the inline findings you selected for this round as ONE grouped batch. The "
                    + "backend validates every anchor before publishing and rejects the batch rather "
                    + "than moving a finding to a different line or folding it into the summary.",
                Parameters =
                [
                    ActionIdParameter(),
                    new FunctionParameterContract
                    {
                        Name = "findings",
                        Description = "The findings to publish, each anchored to a path and a diff line.",
                        ParameterType = JsonSchemaObject.Array(
                            new JsonSchemaObject
                            {
                                Type = new("object"),
                                Required = ["path", "side", "endLine", "body"],
                                Properties = new Dictionary<string, JsonSchemaObject>
                                {
                                    ["path"] = JsonSchemaObject.String(
                                        "Repository-relative file path, exactly as it appears in the diff."
                                    ),
                                    ["side"] = JsonSchemaObject.String(
                                        "Diff side: RIGHT for the new file, LEFT for the old one."
                                    ),
                                    ["startLine"] = JsonSchemaObject.Number(
                                        "Optional first line of a multi-line range. Omit for a single line."
                                    ),
                                    ["endLine"] = JsonSchemaObject.Number(
                                        "The line the comment anchors to, or the last line of the range."
                                    ),
                                    ["body"] = JsonSchemaObject.String("The finding text, published verbatim."),
                                },
                            },
                            "One entry per finding."
                        ),
                        IsRequired = true,
                    },
                    SourcesParameter(),
                ],
            },
            [.. CommonRequiredFields, new("findings", JsonValueKind.Array)]
        ),
        new(
            "PostClarificationQuestion",
            new FunctionContract
            {
                Name = "PostClarificationQuestion",
                Description =
                    "Post one non-blocking clarification question, together with the conclusion you are "
                    + "withholding until it is answered. Asking never blocks the round; publish the rest "
                    + "of the review as usual.",
                Parameters =
                [
                    ActionIdParameter(),
                    BodyParameter("The question and the evidence for the conclusion you are withholding."),
                    ProviderTargetIdParameter(
                        "Optional supplied discussion id, when the question belongs on an existing thread.",
                        isRequired: false
                    ),
                    SourcesParameter(),
                ],
            },
            [.. CommonRequiredFields, new("body", JsonValueKind.String)]
        ),
        new(
            "ReplyToDiscussion",
            new FunctionContract
            {
                Name = "ReplyToDiscussion",
                Description =
                    "Reply to an existing discussion using a target id from the publication context — "
                    + "an answer, a correction, or new evidence. Only reply when it adds value. The "
                    + "backend keeps provider-native thread identity, and reports an explicit "
                    + "degradation in the receipt when a provider can only answer with a flat comment.",
                Parameters =
                [
                    ActionIdParameter(),
                    BodyParameter("Your reply, published verbatim."),
                    ProviderTargetIdParameter(
                        "The supplied discussion id to continue. Never construct one yourself.",
                        isRequired: true
                    ),
                    SourcesParameter(),
                ],
            },
            [.. CommonRequiredFields, new("body", JsonValueKind.String), new("providerTargetId", JsonValueKind.String)]
        ),
        new(
            "FinalizeRound",
            new FunctionContract
            {
                Name = "FinalizeRound",
                Description =
                    "Record that this round is finished, after every action you intended has a receipt "
                    + "or a rejection — including a deliberate decision to publish nothing. Set noOp "
                    + "true only when you published nothing at all; the backend never writes prose on "
                    + "your behalf.",
                Parameters =
                [
                    ActionIdParameter(),
                    new FunctionParameterContract
                    {
                        Name = "noOp",
                        Description =
                            "True when this round deliberately published nothing; false when it published "
                            + "at least one action.",
                        ParameterType = JsonSchemaObject.Boolean("Whether the round published nothing."),
                        IsRequired = true,
                    },
                    SourcesParameter(),
                ],
            },
            [.. CommonRequiredFields, new("noOp", JsonValueKind.True)]
        ),
    ];

    /// <summary>
    /// Every tool name this provider exposes, in the order spec §6.2 lists them. A host keeps these out
    /// of sub-agent inheritance. Projected from <see cref="Operations"/> — and therefore declared after
    /// it, because a static field initializer that reads a field declared later reads null — so the
    /// exclusion roster and the registered descriptors cannot drift apart.
    /// </summary>
    public static readonly IReadOnlyList<string> ToolNames = [.. Operations.Select(o => o.Name)];

    #endregion
}
