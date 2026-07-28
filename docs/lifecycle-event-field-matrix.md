# Lifecycle Event Field Matrix

The field-by-field wire contract for `AchieveAi.LmDotnetTools.LmLifecycle` at
**protocol major 1**.

This document is for someone writing a subscriber — quite possibly not in C#, and
quite possibly against a build older or newer than the producer's. It answers, for
every field: what JSON type it carries, whether it must be present, whether it may
be null, and what its absence means.

The authority is the code; this is the readable projection of it. The golden-fixture
tests in `tests/LmLifecycle.Tests` pin the exact bytes, so if this document and the
wire ever disagree, the tests are what decide.

Related: [ADR 0002 — Lifecycle event wire contract and versioning](adrs/0002-lifecycle-event-wire-contract.md),
[ADR 0003 — Fail-closed tool approval](adrs/0003-fail-closed-tool-approval.md),
[ADR 0005 — Service-to-service lifecycle delivery](adrs/0005-service-to-service-lifecycle-delivery.md).

---

## 1. Rules that apply to every field

These hold everywhere and are not repeated in the tables.

### Absent and null are the same thing

Null-valued members are **omitted**, never written as `null`. A subscriber that
receives `{"a":1}` and one that receives `{"a":1,"b":null}` must reach the same
conclusion about `b`. Encoding always produces the first form; decoding accepts
both.

So there are exactly two states a field can be in, not three:

| On the wire | Means |
| --- | --- |
| present with a value | reported |
| absent (or explicitly `null`) | **not applicable to this event** — never "unknown", never "zero" |

This matters most for correlation identifiers. `parent_run_id` is missing from a
top-level run's `run_started` because that run *has* no parent, not because the
producer failed to look it up.

### An empty collection is written, and it is not the same as absent

`"sources":[]` is a positive statement: the producer looked, and there were none.
A collection member is never omitted. Only `context_loaded.sources` is affected
today, but the rule is general.

### Zero and false are values, not absences

`"turn_index":0`, `"was_forked":false`, and `"rendered_byte_count":0` are always
written. Do not treat a numeric or boolean member as optional because its value
happens to be the type's default.

### Timestamps

Every timestamp is a JSON string in a fixed ISO 8601 UTC form with exactly seven
fractional digits and a `Z` suffix:

```
2026-07-27T08:30:00.1234567Z
```

Producers normalize to UTC before encoding, so the same instant always produces the
same bytes regardless of the machine's offset. **Decoders accept any valid
offset** — a subscriber sending `2026-07-27T14:00:00.0000000+05:30` back through
this contract gets the same instant, re-encoded to the canonical form. This is a
deliberate asymmetry: strict on the way out (bytes are signed), lenient on the way
in.

### Unknown values are preserved, except in an approval decision

Fields marked **open vocabulary** below may carry values this document does not
list. A build that meets one must keep it verbatim, round-trip it byte-identically,
and forward it. Refusing an unrecognized value would make every added event type a
breaking change.

The single exception is `ToolApprovalDecision.decision`, where an unrecognized
value **fails closed** — it does not permit execution. See §6 and ADR 0003.

### Numeric ranges

`source_sequence` and `delivery_sequence` are 64-bit signed integers that start at
`1`. Token counts and byte counts are 64-bit signed. Turn and message counts are
32-bit signed. No field uses a floating-point JSON number.

### The decode rule that keeps defaults intact

Every serialized member of every contract type declares `{ get; set; }` — never
`{ get; init; }`.

This is load-bearing, not stylistic. `System.Text.Json` source generation cannot
assign an init-only property, so it treats each one as a constructor parameter and
builds the object from a full argument list; a member the JSON omits is then
assigned `default`, **overwriting the property's declared initializer**. The
observable damage is that a decoded payload's non-nullable `string` members come
back `null`, in defiance of their own annotations, and a consumer that trusts the
annotation dereferences null. With `set`, the generator emits a plain object
creator, initializers run, and an absent member keeps the default this document
promises.

`LifecycleFieldSemanticsTests.No_member_declared_non_nullable_ever_decodes_to_null`
enforces it. Changing any member back to `init` fails that test.

### Requiredness has two different meanings here

- **Envelope and approval members marked required** carry `[JsonRequired]`.
  Decoding a body that omits one **throws**. These are the fields without which the
  event cannot be routed, ordered, or authorized.
- **No payload member is required.** A payload decodes from `{}` with every member
  at its declared default. That is intentional — it keeps a V1 build able to read a
  V1.1 payload that dropped a field it no longer emits — but it has a sharp
  consequence: *any* JSON object decodes into *any* payload type without
  complaint. Payload type is therefore selected by `event_type`, never inferred
  from the payload's shape. `LifecycleSerializer.TryReadPayload<T>` checks the
  discriminator first and returns `false` on a mismatch rather than handing back a
  plausible-looking object full of defaults.

---

## 2. `LifecycleEventEnvelope` — the event as the producer made it

Every field here is minted **once**, before fan-out. Every subscriber receives the
same values, which is what makes a retry a re-send of an identical body rather than
a new event.

| JSON field | Type | Required | Nullable | Default when absent | Notes |
| --- | --- | --- | --- | --- | --- |
| `schema_major` | number (int32) | ✅ | — | — | Protocol major governing this shape. Currently `1`. A major this build does not support is refused at registration, not per event. |
| `event_id` | string | ✅ | — | — | Globally unique, assigned once, never regenerated. Two deliveries with the same value are the same event — this is the deduplication key. |
| `event_type` | string | ✅ | — | — | **Open vocabulary.** Names the payload shape. See §4. |
| `source_stream_id` | string | ✅ | — | — | `{kind}:{id}` — `thread:{ThreadId}` or `sandbox:{SessionId}`. An **ordering key only**; an owner is never inferred from it. Kind is open vocabulary; the `{kind}:{id}` shape is not. |
| `source_sequence` | number (int64) | ✅ | — | — | Position within `source_stream_id`, starting at `1`, `+1` per event within a producer epoch. **A gap means a dropped event** — that is how loss is detected. |
| `producer_epoch` | string | ✅ | — | — | Identifies the producer incarnation that allocated `source_sequence`. Counters restart when a producer restarts; a changed epoch is how a subscriber tells a restart from a gap. |
| `occurred_at` | string (timestamp) | ✅ | — | — | When the producer observed the event. Descriptive — `source_sequence`, not this, defines order. |
| `correlation` | object | ❌ | ✅ | absent | See §3. Absent when no correlation applies. |
| `payload` | object | ❌ | ✅ | absent | Held as **raw JSON**, which is what lets a subscriber forward and store an event whose type it was never compiled against. Shape is decided by `event_type`. |

`EnsureValid()` throws `LifecycleContractException` when an identifier is empty,
`source_sequence` is not positive, `source_stream_id` is not a well-formed
`{kind}:{id}` pair, or `schema_major` is unsupported. It deliberately does **not**
validate the payload: an unrecognized `event_type` is valid.

### What is *not* on the envelope

There is **no owner, tenant, or application key** — not here and not in
`correlation`. Tenancy is resolved by the host from the authenticated caller. An
event body therefore cannot disclose the tenancy of the stream it came from, and a
caller cannot assert an owner by populating a field. See ADR 0005.

---

## 3. `LifecycleCorrelation` — every member optional

Which correlations exist depends on the event. `sandbox_created` has a session and
a workspace but no turn; a top-level `run_started` has no parent. Absent means
**"does not apply."**

| JSON field | Type | Required | Nullable | Notes |
| --- | --- | --- | --- | --- |
| `thread_id` | string | ❌ | ✅ | The conversation thread. |
| `run_id` | string | ❌ | ✅ | The run. |
| `parent_run_id` | string | ❌ | ✅ | The run that caused this one — resumed, delayed-result, or sub-agent child. **Lineage only**; whether the child inherited provider context is answered by `run_started.was_forked`. |
| `generation_id` | string | ❌ | ✅ | The turn. |
| `tool_call_id` | string | ❌ | ✅ | The tool call. |
| `sub_agent_id` | string | ❌ | ✅ | Present when a sub-agent produced the event. |
| `parent_thread_id` | string | ❌ | ✅ | The thread of the agent that spawned this sub-agent. |
| `spawning_tool_call_id` | string | ❌ | ✅ | The tool call that spawned this sub-agent. **Nullable even for a sub-agent** — one created directly by a host, rather than by a model-requested tool call, has a parent but no spawning call. |
| `sandbox_session_id` | string | ❌ | ✅ | The sandbox session. |
| `workspace_id` | string | ❌ | ✅ | The workspace the session belongs to. |

---

## 4. Payloads

`event_type` selects the payload type. The six below are V1's complete set — the
contract is deliberately bounded, and **it is not an audit log**.

| `event_type` | Payload | Emitted when |
| --- | --- | --- |
| `run_started` | `RunStartedPayload` | A run begins. |
| `context_loaded` | `ContextLoadedPayload` | Discovered context is rendered into a provider request, immediately before dispatch. |
| `turn_completed` | `TurnCompletedPayload` | A turn reaches a final state. Exactly one per accepted `generation_id`. |
| `tool_completed` | `ToolCompletedPayload` | A tool call reaches a final state. |
| `run_completed` | `RunCompletedPayload` | A run reaches a terminal state. |
| `sandbox_created` | `SandboxCreatedPayload` | A sandbox session is committed, after create or recreate. |

An `event_type` outside this list is **not an error**. Keep the event, forward it,
store it; `LifecycleEventEnvelope.IsKnownEventType` reports `false` and
`TryReadPayload<T>` returns `false`.

Reminder from §1: **no payload member is required**, and every member below has a
declared default that survives an absent member.

### 4.1 `run_started`

| JSON field | Type | Nullable | Default when absent | Notes |
| --- | --- | --- | --- | --- |
| `run_id` | string | ❌ | `""` | The run that started. |
| `generation_id` | string | ❌ | `""` | The first turn's generation id. |
| `cause` | object | ❌ | `{"kind":"user_input"}` | See below. Always written. |
| `was_forked` | bool | ❌ | `false` | Whether the run inherited provider-side context from its parent. Distinct from `parent_run_id`, which is lineage. |
| `agent_kind` | string | ❌ | `""` | **Open vocabulary**: `raw`, `claude`, `codex`, `copilot`. |
| `model_id` | string | ✅ | absent | The configured model, when the host knows it. |

`cause` (`LifecycleRunCause`):

| JSON field | Type | Nullable | Default | Notes |
| --- | --- | --- | --- | --- |
| `kind` | string | ❌ | `"user_input"` | **Open vocabulary**: `user_input`, `tool_result`, `sub_agent_spawn`. |
| `tool_call_id` | string | ✅ | absent | The originating call, for `tool_result` and `sub_agent_spawn`. Absent for `user_input`. |

### 4.2 `context_loaded`

Emitted from the **immutable provider-request snapshot**, immediately before
dispatch — so it describes context that was actually sent, not context that was
merely discovered. A request that is queued, cancelled, or rediscovered without
being dispatched produces no event.

| JSON field | Type | Nullable | Default when absent | Notes |
| --- | --- | --- | --- | --- |
| `run_id` | string | ❌ | `""` | The run whose request carried the context. |
| `generation_id` | string | ❌ | `""` | The turn whose request carried the context. |
| `sources` | array of object | ❌ | `[]` | **Always written, including when empty.** See below. |
| `rendered_hash` | string | ❌ | `""` | Hash of the exact rendered block as sent. |
| `rendered_byte_count` | number (int64) | ❌ | `0` | Byte length of the rendered block as sent. |
| `rendered_text` | string | ✅ | absent | **Capability-gated.** Present only for subscribers granted `lifecycle.content.full`; otherwise omitted entirely, leaving the hash as the only description. Absent here means "not granted", not "empty". |

`sources[]` (`LifecycleContextSource`):

| JSON field | Type | Nullable | Default | Notes |
| --- | --- | --- | --- | --- |
| `discovery_kind` | string | ❌ | `""` | **Open vocabulary** — how the source was found. |
| `name` | string | ❌ | `""` | Short display name. |
| `normalized_path` | string | ✅ | absent | Absent for a source that has no path. |
| `dedup_identity` | string | ❌ | `""` | Identity used to collapse duplicate discoveries. |
| `rendered_byte_count` | number (int64) | ❌ | `0` | Bytes rendered **after** any truncation. |
| `was_truncated` | bool | ❌ | `false` | Whether the source was cut to fit. |
| `phase` | string | ❌ | `"boot"` | **Open vocabulary**: `boot`, `mid_session`. |

### 4.3 `turn_completed`

Exactly one per accepted `generation_id`, on every path — success, error, cancel,
interruption, and max-turn alike. It is **final-only**: it never carries fragments,
and a partial or failed turn exposes only complete messages plus a stable outcome.

| JSON field | Type | Nullable | Default when absent | Notes |
| --- | --- | --- | --- | --- |
| `run_id` | string | ❌ | `""` | The run this turn belongs to. |
| `generation_id` | string | ❌ | `""` | The turn that completed. |
| `turn_index` | number (int32) | ❌ | `0` | Ordinal within the run, starting at `1`. Always written. |
| `outcome` | string | ❌ | `"completed"` | **Open vocabulary**: `completed`, `error`, `cancelled`, `interrupted`. |
| `message_count` | number (int32) | ❌ | `0` | Complete messages the turn produced. Always written. |
| `tool_call_count` | number (int32) | ❌ | `0` | Tool calls the turn requested. Always written. |
| `usage` | object | ✅ | absent | See §5.1. Absent when the provider reported none. |
| `error` | object | ✅ | absent | See §5.2. Absent unless `outcome` indicates failure. |

### 4.4 `tool_completed`

| JSON field | Type | Nullable | Default when absent | Notes |
| --- | --- | --- | --- | --- |
| `run_id` | string | ❌ | `""` | The run that requested the call. |
| `generation_id` | string | ✅ | absent | Nullable: a delayed result is committed after its originating turn has ended. |
| `tool_call_id` | string | ❌ | `""` | The call that completed. |
| `tool_name` | string | ❌ | `""` | Registered tool name. |
| `tool_kind` | string | ❌ | `"host"` | **Open vocabulary**: `host`, `provider`, `sub_agent`. |
| `outcome` | string | ❌ | `"succeeded"` | **Open vocabulary**: `succeeded`, `failed`, `denied`, `cancelled`. |
| `was_deferred` | bool | ❌ | `false` | Whether the result arrived after the turn that requested it. See ADR 0004. |
| `duration_ms` | number (int64) | ✅ | absent | Dispatch to final state. |
| `approval` | object | ✅ | absent | See below. **Absent means no gate was opened** — not that approval was skipped or implied. |
| `error` | object | ✅ | absent | See §5.2. |

`approval` (`ToolApprovalSummary`):

| JSON field | Type | Nullable | Default | Notes |
| --- | --- | --- | --- | --- |
| `decision` | string | ❌ | `""` | The recorded outcome. See §6.1 for the full code list. |
| `arguments_hash` | string | ❌ | `""` | The frozen argument hash the decision applied to. |
| `decided_by` | string | ✅ | absent | Which approver decided, when the host records it. |
| `wait_ms` | number (int64) | ✅ | absent | How long the call waited for a decision. |

### 4.5 `run_completed`

| JSON field | Type | Nullable | Default when absent | Notes |
| --- | --- | --- | --- | --- |
| `run_id` | string | ❌ | `""` | The run that completed. |
| `generation_id` | string | ❌ | `""` | The run's originating generation id. |
| `outcome` | string | ❌ | `"completed"` | **Open vocabulary**: `completed`, `error`, `cancelled`, `interrupted`, `max_turns`, `awaiting_sibling_results`. The last marks a zero-turn sibling in a delayed-result fan-in (ADR 0004). |
| `turn_count` | number (int32) | ❌ | `0` | Turns the run performed. Always written. |
| `usage` | object | ✅ | absent | See §5.1. |
| `error` | object | ✅ | absent | See §5.2. |

### 4.6 `sandbox_created`

Emitted after a **committed** create or recreate, from outside the registry's
locks — a failed or rolled-back attempt produces no event.

| JSON field | Type | Nullable | Default when absent | Notes |
| --- | --- | --- | --- | --- |
| `session_id` | string | ❌ | `""` | The committed session. |
| `workspace_id` | string | ❌ | `""` | The workspace it belongs to. |
| `was_recreated` | bool | ❌ | `false` | Whether this replaced an earlier session. |
| `replaced_session_id` | string | ✅ | absent | The session replaced. Absent when `was_recreated` is `false`. |
| `status` | string | ❌ | `""` | **Open vocabulary** — gateway-reported status at commit. |
| `image_reference` | string | ✅ | absent | The image the session runs, when known. |
| `inventory` | object | ❌ | see §5.3 | What the gateway confirmed it loaded. **Always written**, so an old gateway is never mistaken for an empty session. |

---

## 5. Shared payload types

### 5.1 `LifecycleUsage`

| JSON field | Type | Nullable | Default | Notes |
| --- | --- | --- | --- | --- |
| `prompt_tokens` | number (int64) | ❌ | `0` | Input tokens as the provider reported them. |
| `completion_tokens` | number (int64) | ❌ | `0` | Output tokens as the provider reported them. |
| `total_tokens` | number (int64) | ❌ | `0` | Total as the provider reported it. |
| `cached_prompt_tokens` | number (int64) | ✅ | absent | Absent when the provider does not report caching — **not `0`**, which would assert no cache hits. |
| `reasoning_tokens` | number (int64) | ✅ | absent | Same rule. |
| `completeness` | string | ❌ | `"in_progress"` | **Open vocabulary**: `in_progress`, `partial`, `complete`. |

`completeness` exists so a partial total is never mistaken for a final one. Providers
report usage at different points and with different fidelity; a subscriber
aggregating cost must check this before treating the counts as settled.

### 5.2 `LifecycleError`

| JSON field | Type | Nullable | Default | Notes |
| --- | --- | --- | --- | --- |
| `code` | string | ❌ | `""` | **Open vocabulary**, stable, low-cardinality — safe to group and alert on. |
| `message` | string | ✅ | absent | Short human-readable description, **free of sensitive material**. Never parse it; it is not part of the contract. |

### 5.3 `SandboxInventorySummary`

| JSON field | Type | Nullable | Default | Notes |
| --- | --- | --- | --- | --- |
| `status` | string | ❌ | `"unavailable"` | **Open vocabulary**: `confirmed`, `unavailable`. Only the exact value `confirmed` means confirmed. |
| `unavailable_reason` | string | ✅ | absent | Why the inventory could not be confirmed. Present whenever `status` is not `confirmed`. |
| `items` | array | ❌ | `[]` | Confirmed items (§5.4). Always `[]` when `status` is not `confirmed`. |

**Confirmed means loaded — not requested, and not available.** A create request naming three
marketplaces may load none of them, and a marketplace catalog describes what a session *could*
load. Neither is ever reported here, because a subscriber acting on this event reads it as a
statement about the session that now exists.

Reporting is fail-closed and mirrors the approval rule in ADR 0003: silence is never upgraded into
a claim. A gateway that omits the block, or that returns items without claiming them confirmed,
produces `unavailable` with a reason and an empty list. That is why `unavailable_reason` is
mandatory in that state and why `items` is `[]` rather than absent — a consumer can always tell
"nothing is loaded" from "nobody could tell us", without a second call.

### 5.4 `SandboxInventoryEntry`

| JSON field | Type | Nullable | Default | Notes |
| --- | --- | --- | --- | --- |
| `kind` | string | ❌ | `""` | **Open vocabulary**: `plugin`, `skill`, `agent`. |
| `id` | string | ❌ | `""` | The gateway's identifier, unique within its `kind`. |
| `version` | string | ✅ | absent | The loaded version, when the gateway tracks one for this kind. |

Identity and version only. Manifests, descriptions, install paths, source repositories, and
publisher metadata are excluded by the §1 allowlist rule: the event stream has a different audience
from the sandbox, and knowing *what* is loaded never requires shipping the content of it.

---

## 6. Approval types

These do not travel as lifecycle events. They are the request/response pair between
a host and an approver, encoded by the same contract. See ADR 0003.

### 6.1 `ToolApprovalRequest`

| JSON field | Type | Required | Nullable | Notes |
| --- | --- | --- | --- | --- |
| `request_id` | string | ✅ | — | Identity of this request. A second decision naming it is resolved against the first, not applied over it. |
| `thread_id` | string | ❌ | ❌ (`""`) | The thread the call belongs to. |
| `run_id` | string | ❌ | ❌ (`""`) | The run that requested the call. |
| `generation_id` | string | ❌ | ❌ (`""`) | The turn that requested the call. |
| `tool_call_id` | string | ✅ | — | The call awaiting a decision. |
| `tool_name` | string | ❌ | ❌ (`""`) | Registered tool name. |
| `arguments_hash` | string | ✅ | — | SHA-256 over the UTF-8 bytes of the exact argument string that will execute, lowercase hex. **Frozen when the gate opened.** |
| `arguments` | string | ❌ | ✅ | **Capability-gated** on `lifecycle.content.full`. Absent leaves `arguments_hash` as the only description of what will run. |
| `expires_at` | string (timestamp) | ✅ | — | Already reduced to the **effective** expiry: the earliest of the configured maximum wait, any provider or host deadline, and the lifetime of the requesting run and turn. A pending approval can never outlive its run. |

A request describes **one invocation**, not a tool or a category. Approving it
approves exactly the bytes named by `arguments_hash`.

### 6.2 `ToolApprovalDecision`

| JSON field | Type | Required | Nullable | Notes |
| --- | --- | --- | --- | --- |
| `request_id` | string | ✅ | — | The request being answered. |
| `decision` | string | ✅ | — | **Fails closed** — see below. |
| `arguments_hash` | string | ✅ | — | Echoed from the request. A mismatch means the approver decided about different bytes than the ones that would run, so the decision is refused. |
| `reason` | string | ❌ | ✅ | Short rationale, free of sensitive material. |
| `decided_at` | string (timestamp) | ❌ | ✅ | When the approver decided. |

### 6.3 Outcome codes — the one place unknown values fail closed

| Code | Approver may submit | Permits execution |
| --- | --- | --- |
| `allowed` | ✅ | ✅ **— the only value that does** |
| `denied` | ✅ | ❌ |
| `timeout` | ❌ | ❌ |
| `missing_approver` | ❌ | ❌ |
| `overload` | ❌ | ❌ |
| `hook_error` | ❌ | ❌ |
| `revoked` | ❌ | ❌ |
| `cancelled` | ❌ | ❌ |
| `host_policy_denied` | ❌ | ❌ |
| `provider_policy_denied` | ❌ | ❌ |
| *anything else* | ❌ | ❌ |

`ToolApprovalOutcomes.IsAllowed` matches the **exact ordinal string** `allowed`.
`ALLOWED`, `allowed ` (trailing space), `""`, `null`, and
`allowed_by_some_future_policy` all fail to permit execution.

This is the deliberate inverse of §1's preservation rule, and the asymmetry is the
point: an unknown *descriptive* value is kept because discarding it loses
information, while an unknown *authorization* value is refused because honoring it
would grant permission this build cannot verify. A future major that adds a
conditional grant must introduce a **new field**; it must not add a new value to
`decision` and expect old builds to honor it. They will not.

Codes other than `allowed`/`denied` are host-generated: `provider_policy_denied`
and `host_policy_denied` are decided before any approver is asked, so no gate is
ever opened for them.

---

## 7. `LifecycleDeliveryEnvelope` — the event as one subscriber receives it

Source identity and delivery identity are separate. The inner event is byte-identical
for every recipient; these two fields belong to one subscriber alone.

| JSON field | Type | Required | Nullable | Notes |
| --- | --- | --- | --- | --- |
| `delivery_id` | string | ✅ | — | Minted once, before the first attempt. A retry **reuses it and re-sends a byte-identical body** — which keeps a signature over the original bytes valid and lets a receiver recognize a retry instead of double-processing it. |
| `delivery_sequence` | number (int64) | ✅ | — | Position in this subscriber's own stream, starting at `1`. |
| `event` | object | ✅ | — | The producer's `LifecycleEventEnvelope`, unchanged. |

**Delivery numbering happens after filtering.** A subscriber is numbered only across
the events it was entitled to receive, so its `delivery_sequence` is contiguous and a
gap in it means loss specific to it — without disclosing that events it was never
entitled to see exist at all.

The two sequences answer different questions, and a subscriber needs both:

| Question | Field |
| --- | --- |
| Did the producer drop an event? | `source_sequence` gap (within one `producer_epoch`) |
| Did *my* delivery pipeline drop one? | `delivery_sequence` gap |
| Did the producer restart? | `producer_epoch` changed |
| Have I already handled this? | `event_id` seen (or `delivery_id` for retry detection) |

---

## 8. Changing this contract

Within major 1, these are **additive** and safe:

- A new `event_type`, with a new payload type.
- A new optional payload member.
- A new value in an open vocabulary — **except** `ToolApprovalDecision.decision`.

These are **breaking** and require a new major:

- Removing or renaming a field.
- Changing a field's JSON type.
- Making an existing field required.
- Changing the meaning of an existing value.
- Adding a value to `ToolApprovalDecision.decision` and expecting old builds to
  honor it.

Any change to the encoding — property names, timestamp format, null handling —
changes the bytes, and the golden-fixture tests will fail. That failure is the
review gate: update the fixtures in the same commit, deliberately, or fix the
encoder. The public-API baseline in `tests/LmLifecycle.Tests/PublicApi.Shipped.txt`
plays the same role for the C# surface, including constant *values*, which a
precompiled consumer bakes into its own metadata.
