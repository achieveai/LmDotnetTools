using System.Buffers;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmLifecycle;

namespace AchieveAi.LmDotnetTools.LmAgentInfra.Lifecycle;

/// <summary>
/// The <see cref="LifecycleCapabilities.ContentFull"/> gate. A subscription that was not granted
/// <c>lifecycle.content.full</c> receives the shape of what happened — outcomes, counts, durations,
/// hashes, identifiers — but none of the text that flowed through the agent.
/// <para>
/// <b>This is an allow-list projection, not a deny-list.</b> The payload is re-projected keeping
/// only the property names explicitly classified as safe; everything else is dropped, and an event
/// type with no allow-list has its payload withheld entirely. The difference matters on the day
/// someone adds a field to a payload type: a deny-list ships that field to every subscriber until
/// somebody notices, whereas an allow-list withholds it until somebody classifies it. Silence
/// should cost a subscriber a field, not cost a tenant its content.
/// </para>
/// <para>
/// The envelope's own members — ids, sequences, timestamps, correlation — are never touched. They
/// are identity, not content: a subscriber that cannot see <c>delivery_sequence</c> cannot detect
/// the gaps this pipeline deliberately produces, and a subscriber that cannot see <c>run_id</c>
/// cannot correlate what it is being told with what it started.
/// </para>
/// </summary>
public sealed class LifecycleContentRedactor
{
    /// <summary>
    /// A node in the allow-list tree. A property mapped to <see langword="null"/> is a leaf and is
    /// copied verbatim; a property mapped to a node is recursed into, keeping only that node's own
    /// properties. Arrays apply their node to every element.
    /// </summary>
    private sealed class AllowList
    {
        private readonly Dictionary<string, AllowList?> _properties;

        internal AllowList(params (string Name, AllowList? Child)[] properties)
        {
            _properties = new Dictionary<string, AllowList?>(StringComparer.Ordinal);
            foreach (var (name, child) in properties)
            {
                _properties[name] = child;
            }
        }

        internal bool TryGetChild(string name, out AllowList? child) => _properties.TryGetValue(name, out child);
    }

    // Shared sub-shapes. LifecycleUsage is pure arithmetic, so all of it is safe. LifecycleError is
    // deliberately split: `code` is an open-vocabulary constant that says what class of thing went
    // wrong, while `message` is free text that routinely carries a file path, a prompt fragment, or
    // a provider's echo of the request — the exact material the capability gates.
    private static readonly AllowList UsageAllowList = new(
        ("prompt_tokens", null),
        ("completion_tokens", null),
        ("total_tokens", null),
        ("cached_prompt_tokens", null),
        ("reasoning_tokens", null),
        ("completeness", null)
    );

    private static readonly AllowList ErrorAllowList = new(("code", null));

    private readonly Dictionary<string, AllowList> _allowListsByEventType;

    /// <summary>Creates a redactor over the built-in per-event-type allow-lists.</summary>
    public LifecycleContentRedactor() =>
        _allowListsByEventType = new Dictionary<string, AllowList>(StringComparer.Ordinal)
        {
            // What the agent *is* — the model it runs, the kind of agent it is, why it started — is
            // configuration the subscriber already chose. What the agent *saw* is withheld.
            [LifecycleEventTypes.RunStarted] = new(
                ("run_id", null),
                ("generation_id", null),
                ("cause", new AllowList(("kind", null), ("tool_call_id", null))),
                ("was_forked", null),
                ("agent_kind", null),
                ("model_id", null)
            ),
            // `rendered_text` is the assembled system context: the single largest concentration of
            // user content in the whole event set. `name`, `normalized_path`, and `dedup_identity`
            // are withheld too — a repository layout is host detail, and dedup identity is derived
            // from the path.
            [LifecycleEventTypes.ContextLoaded] = new(
                ("run_id", null),
                ("generation_id", null),
                ("rendered_hash", null),
                ("rendered_byte_count", null),
                (
                    "sources",
                    new AllowList(
                        ("discovery_kind", null),
                        ("rendered_byte_count", null),
                        ("was_truncated", null),
                        ("phase", null)
                    )
                )
            ),
            [LifecycleEventTypes.TurnCompleted] = new(
                ("run_id", null),
                ("generation_id", null),
                ("turn_index", null),
                ("outcome", null),
                ("message_count", null),
                ("tool_call_count", null),
                ("usage", UsageAllowList),
                ("error", ErrorAllowList)
            ),
            // `tool_name` is kept: it is a registered tool identifier from a closed set the host
            // published, not caller-supplied text, and without it "a tool failed" is unactionable.
            // Tool *arguments* never appear on this payload at all — only `arguments_hash`, which is
            // what makes an approval auditable without disclosing what was approved.
            [LifecycleEventTypes.ToolCompleted] = new(
                ("run_id", null),
                ("generation_id", null),
                ("tool_call_id", null),
                ("tool_name", null),
                ("tool_kind", null),
                ("outcome", null),
                ("was_deferred", null),
                ("duration_ms", null),
                (
                    "approval",
                    new AllowList(("decision", null), ("arguments_hash", null), ("decided_by", null), ("wait_ms", null))
                ),
                ("error", ErrorAllowList)
            ),
            [LifecycleEventTypes.RunCompleted] = new(
                ("run_id", null),
                ("generation_id", null),
                ("outcome", null),
                ("turn_count", null),
                ("usage", UsageAllowList),
                ("error", ErrorAllowList)
            ),
            // The session and workspace ids are already envelope correlation, so repeating them
            // discloses nothing new. Everything describing the *contents* of the environment is
            // withheld: `image_reference` is a registry URL, `unavailable_reason` is error text, and
            // the inventory's `id`/`version` enumerate the host's installed toolchain. Only the
            // per-entry `kind` survives, which answers "was anything provisioned" without answering
            // "what exactly is in there".
            [LifecycleEventTypes.SandboxCreated] = new(
                ("session_id", null),
                ("workspace_id", null),
                ("was_recreated", null),
                ("replaced_session_id", null),
                ("status", null),
                ("inventory", new AllowList(("status", null), ("items", new AllowList(("kind", null)))))
            ),
        };

    /// <summary>
    /// Returns the envelope a specific subscription may see.
    /// </summary>
    /// <param name="lifecycleEvent">The event as produced. Never mutated.</param>
    /// <param name="subscription">The subscription whose granted capabilities decide the result.</param>
    /// <returns>
    /// <paramref name="lifecycleEvent"/> unchanged when the subscription holds
    /// <see cref="LifecycleCapabilities.ContentFull"/>; otherwise a copy whose payload has been
    /// projected onto the allow-list, or whose payload is <see langword="null"/> when the event type
    /// has no allow-list.
    /// </returns>
    public LifecycleEventEnvelope Redact(LifecycleEventEnvelope lifecycleEvent, LifecycleSubscription subscription)
    {
        ArgumentNullException.ThrowIfNull(lifecycleEvent);
        ArgumentNullException.ThrowIfNull(subscription);

        if (subscription.HasCapability(LifecycleCapabilities.ContentFull))
        {
            return lifecycleEvent;
        }

        if (
            lifecycleEvent.Payload is not { } payload
            || payload.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
        )
        {
            return lifecycleEvent;
        }

        // An unknown event type reaches here whenever a newer producer emits something this build
        // has never heard of. Its payload is withheld rather than passed through, because "we do not
        // know what is in it" is not a reason to forward it to a subscriber that was not granted
        // content.
        if (
            string.IsNullOrEmpty(lifecycleEvent.EventType)
            || !_allowListsByEventType.TryGetValue(lifecycleEvent.EventType, out var allowList)
        )
        {
            return lifecycleEvent with { Payload = null };
        }

        return lifecycleEvent with
        {
            Payload = Project(payload, allowList),
        };
    }

    private static JsonElement Project(JsonElement payload, AllowList allowList)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            WriteProjected(writer, payload, allowList);
        }

        // Cloned so the element survives the JsonDocument it was parsed from; the caller keeps it on
        // an envelope that outlives this method.
        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    private static void WriteProjected(Utf8JsonWriter writer, JsonElement element, AllowList allowList)
    {
        if (element.ValueKind is JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var property in element.EnumerateObject())
            {
                if (!allowList.TryGetChild(property.Name, out var child))
                {
                    continue;
                }

                writer.WritePropertyName(property.Name);
                if (child is null)
                {
                    property.Value.WriteTo(writer);
                }
                else
                {
                    WriteProjected(writer, property.Value, child);
                }
            }

            writer.WriteEndObject();
            return;
        }

        if (element.ValueKind is JsonValueKind.Array)
        {
            writer.WriteStartArray();
            foreach (var item in element.EnumerateArray())
            {
                WriteProjected(writer, item, allowList);
            }

            writer.WriteEndArray();
            return;
        }

        // The allow-list expected a structure and found a scalar, so the payload does not have the
        // shape this classification was written against. Withholding is the only safe answer: an
        // unrecognized shape is precisely the case where "it is probably fine" has never been
        // verified.
        writer.WriteNullValue();
    }
}
