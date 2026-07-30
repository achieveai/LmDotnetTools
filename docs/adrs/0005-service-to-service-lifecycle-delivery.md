# ADR 0005: Scope service-to-service lifecycle delivery to a host-resolved owner key

* Status: Accepted
* Date: 2026-07-27
* Related issues, PRs, or commits: [#227](https://github.com/achieveai/LmDotnetTools/issues/227)

## Context

Lifecycle events ([ADR 0002](0002-lifecycle-event-wire-contract.md)) must reach
subscribers outside the host process, and approval decisions ([ADR 0003](0003-fail-closed-tool-approval.md))
must come back from them. That turns an in-process observation stream into an
authenticated network surface, which raises two questions the in-process case never had to
answer: who is allowed to receive a given event, and how is a delivery authenticated in
both directions.

The isolation model already exists in this codebase and is consistent. A caller's identity
is `SandboxCredential.AppId`, and it is frozen at four points: the agent pool entry
(`MultiTurnAgentPool.cs:126`, assigned once at `:1223`), the established sandbox binding
(`SandboxSessionRegistry.cs:71`, compared at `:398-408`), the per-session credential map
(`:1084`), and the session cache partition key `(WorkspaceId, AppId)` (`:641`). A caller
credential enters only as a per-request value read from headers
(`ConversationsController.cs:822`) and is documented as never persisted to thread metadata
and never logged. A mismatch between the binding owner and the requesting caller is a
409 conflict, not a silent takeover.

Signing infrastructure also already exists, and its history is instructive. A complete,
unit-tested HMAC stack lives in `samples/CodeReviewDaemon.Sample/Auth/` —
`WebhookSigningSecret`, `WebhookRequestVerifier`, `WebhookVerificationMiddleware`,
`DeliveryReplayCache`. It is deliberately **not registered**
(`CodeReviewDaemon.Sample/Program.cs:164`, `:736`) because the sandbox gateway sends only
`Authorization: {gateway_auth}` with no signature, timestamp, or delivery-id headers, so
enabling verification rejected every genuine callback. It was retained for a future
*signing* producer.

What does not exist anywhere in the repository is key rotation.
`WebhookSigningSecret` holds a single key for process lifetime with no key-id concept, and
`docs/deployment/AUTH_ENFORCE.md:128,136` records that dual-key rotation was considered
and consciously not implemented.

## Decision

**Subscriptions are scoped by an opaque `LifecycleOwnerKey`.** The key wraps the
normalized application identity already frozen as `SandboxCredential.AppId`, inheriting
that model rather than inventing a second notion of tenancy. Its handling is deliberately
narrow:

* It is resolved **only** by a host-supplied resolver.
* It is **never** deserialized from a payload — a caller cannot assert its own owner.
* It is **never** inferred from a thread, run, session, workspace, or tool identifier,
  because those identifiers are visible to parties who are not the owner and inferring
  from them would let a known id substitute for authentication.
* It is **never** serialized onto an envelope, so an event body cannot disclose the
  tenancy of the stream it came from.
* If two resolvers disagree about the owner of the same subject, resolution **fails
  closed**.

**Ownership derives from the *calling* credential, never the effective one.** The default
resolver reads a thread's established sandbox binding, which carries two credentials that
are easy to confuse: the effective credential, always populated and falling back to the
process default, and the caller credential, populated only when a service actually
authenticated as itself. Only the latter confers ownership. Reading the effective
credential instead would make every interactive conversation in the process resolve to the
process's own app id, so any service that authenticated as that same app would receive
lifecycle events for interactive users' conversations — a cross-tenant disclosure produced
by a one-word difference. The consequence is deliberate: interactive conversations have no
remote owner and are never delivered off-box. A host that wants them observable must say so
with its own resolver rather than inherit it from a fallback.

Resolution is asymmetric between observation and authorization, and the asymmetry is the
point. Resolving the owner of an *event* falls back to the spawning thread when a
sub-agent's own thread never established a binding; without that, every sub-agent event
would be dropped silently, which reads as a delivery bug rather than the policy it would
actually be. Resolving the owner of an *approval* does not fall back, because that path
decides who may authorize rather than who may watch, and inheriting upward would let a
parent conversation's subscriber approve a tool call it was never shown. Neither path
infers anything from the envelope: both identifiers are used solely as keys into the host's
own binding map, and a thread absent from that map yields nothing regardless of what the
envelope claims.

Every event is filtered against the subscriber's owner key **before** delivery numbering,
which is what makes each subscriber's `delivery_sequence` contiguous and its loss
detection specific to itself (see [ADR 0002](0002-lifecycle-event-wire-contract.md)).

**Payload content is capability-gated through an allow-list projection.** Owning an event
entitles a subscriber to know that it happened, not necessarily to read what was in it.
A subscriber granted the dedicated `lifecycle.content.full` capability receives payloads
intact; every other subscriber receives the same event with content replaced by hashes and
counts, which preserves correlation, ordering, and loss detection while disclosing nothing
quotable. Envelope fields are never projected away — they carry no content, and redacting
them would break the sequence reasoning above.

The projection is an **allow-list, not a deny-list**, and that direction is the whole
decision. Under a deny-list, adding a field to an event payload discloses it to every
subscriber by default, and the omission is silent: nothing fails, no test goes red, and the
leak is bounded only by how long it takes someone to notice. Under an allow-list, the same
omission withholds a field that should have been sent, which surfaces as a subscriber
reporting missing data — visible, attributable, and harmless in the meantime. An event type
with no allow-list entry at all therefore has its entire payload withheld rather than
passed through, so a new event type is born closed. Producer-controlled structural
credentials — tokens, keys, signed URLs, and anything else the producer minted rather than
observed — are stripped ahead of the projection regardless of capability, because no
subscriber's entitlement to see an event extends to reusing the producer's authority.

**The existing signing stack is promoted, not reimplemented.** The four types move from
the sample into the shared infrastructure library and become the S2S signing primitives.
The wire format is unchanged: HMAC-SHA256 over `{timestamp}.{deliveryId}.{rawBody}`,
lowercase hex, carried in `X-Sandbox-Signature` / `X-Sandbox-Timestamp` /
`X-Sandbox-Delivery-Id`, with a ±5 minute skew window and a replay cache whose TTL is
twice the skew tolerance so a replay can never outlive the freshness window. Signing over
raw body bytes rather than re-serialized JSON is what allows a retry to re-send an
identical signed body, which is the same requirement that forced deterministic
serialization in ADR 0002.

**Rotation is added as dual-key verification.** Signing always uses the current key;
verification accepts the current or the previous key during an overlap window, so
rotating does not reject deliveries already in flight. Revocation drops a key from the
active set immediately. An unconfigured deployment stays fail-closed — the existing
random-key fallback means nothing verifies rather than everything being accepted.

**Signing secrets are minted by the host, disclosed once, and never readable again.** The
subscriber does not choose its own secret and cannot supply one; the host generates 256
bits from a cryptographic RNG and returns the value exactly once, in the response to the
create or rotate call that produced it. Nothing else ever returns it. A subscriber that
loses its secret rotates rather than retrieves, which means a leaked read path cannot exist
because there is no read path. Subscription identifiers come from the same generator rather
than `Guid.NewGuid` — not because a guessed identifier grants anything on its own, since
every control-plane call is owner-checked first, but because an enumerable identifier space
turns those checks into the only thing standing between a caller and someone else's
subscription, and a CSPRNG costs nothing here.

This is also why the control plane is deliberately minimal: register, rotate, revoke, and
nothing that lists or reads back. There is no broad subscription inspection API, so an
authenticated caller cannot enumerate what else exists — and unknown and foreign
subscriptions are reported identically, so a rejection never distinguishes "no such
subscription" from "not yours."

**Callback destinations are authorized on every attempt, not just at registration.**
A callback URL must be HTTPS and its host and port must appear in the configured egress
authorization list. That check runs at registration, again when a delivery is enqueued, and
again on each retry. Re-checking is not redundant: a subscription outlives the
configuration that admitted it, and an operator who narrows the egress list to contain an
incident expects that to take effect now, on in-flight and retrying deliveries, rather than
whenever subscriptions happen to be re-registered.

**A quarantine is held against the destination, not the subscription, and expires on a
clock.** A subscriber whose endpoint fails repeatedly, or which retires itself, has its
queue quarantined and its pending deliveries dropped. Scoping that to the subscription
would make it advisory: revoking and re-registering the same callback mints a new
subscription id and therefore a fresh queue, so a failing endpoint could consume the host's
retry budget indefinitely by re-registering. The quarantine key is therefore scheme, host,
and port — path and query discarded, internationalized hosts normalized to punycode, so
neither a query string nor an alternate spelling of the same name is an escape hatch — and
a subscription registered during the window is admitted but starts quarantined. The window
is bounded (default 15 minutes) rather than permanent, so a brief outage does not become an
operator ticket and the host does not accumulate state that only a restart clears.

**The two directions are authenticated by different mechanisms, and conflating them is the
trap.** Outbound deliveries are authenticated by the host's HMAC signature, which is what
the promoted stack provides. Inbound calls — register, rotate, revoke, and approval
decisions — are authenticated by the *host's own* authentication scheme, and the controllers
read the resulting `HttpContext.User` principal rather than any request header. A controller
cannot distinguish a header that a middleware verified from a header a client simply typed,
so trusting one would mean trusting every deployment to have wired verification correctly,
with no way to detect the deployments that did not. The failure that buys is an
unauthenticated caller naming its own owner key, which is the single worst outcome this
surface has. Reading a principal also keeps the library scheme-agnostic: JWT, mTLS, or a
custom handler all converge on the same input to owner resolution.

The consequence is that webhook signature verification alone does **not** make these
endpoints usable — nothing populates a principal, so every call is rejected. That direction
is correct and is stated on the public types rather than left to be discovered as an
unexplained 403.

**When the feature is off, the control-plane routes do not exist — and turning it on must
not change any other route.** This needs stating because the default is the opposite of
what it looks like in source: the .NET SDK generates an `ApplicationPart` for every
referenced assembly that transitively references MVC, so a host that merely references this
library already publishes its controllers, on a default configuration, with none of their
dependencies registered. Left alone, a host that had never enabled lifecycle delivery would
answer `api/lifecycle/*` with a container failure rather than a 404 — announcing a feature
it does not have. The hosting extension therefore replaces MVC's controller discovery
rather than adding to it (feature providers are unioned, so a narrowing provider added
alongside the default one narrows nothing) and decides visibility from **provenance**: the
gated lifecycle controllers appear only when their flag is set, while everything else in
this assembly keeps exactly the visibility it already had — visible if the host supplied the
application part, absent if the extension added it. Deciding by provenance rather than by a
list of admitted names is what keeps enabling one feature from silently unpublishing a
host's unrelated endpoints, which an allow-list would have done. A host that has already
installed its own controller feature provider is refused loudly, with instructions, because
that case cannot be narrowed without erasing the host's own filter.

**Delivery is best-effort and bounded.** There is no durable outbox, no replay, and no
backfill. Drops are deliberate, reasoned, and observable as a sequence gap.

**Diagnostics carry opaque identifiers only** — never payloads, signatures, headers,
bodies, secrets, or full URLs. This continues established practice: the existing
middleware logs only an 8-character delivery-id prefix
(`WebhookVerificationMiddleware.cs:161`), and `SandboxCredential`, `SandboxAuthProvider`,
and `SandboxDiscoverySettings` all override `ToString()` to emit `[REDACTED]`. Failure
diagnostics must not recurse through lifecycle hooks, and metrics stay low-cardinality —
owner keys and thread identifiers are not metric dimensions.

## Consequences

An external service can subscribe to exactly the events it owns and answer approval
requests, authenticated in both directions, without the host disclosing cross-tenant
activity. Scoping to an identity the codebase already freezes in four places means there
is one tenancy model to reason about, not two.

Promoting the signing stack removes a tested implementation from a sample where it was
dead and puts it where it is needed, which also fixes two latent defects found while
mapping it: the middleware's documentation comment contradicted its own signed-payload
format by omitting the delivery id, and the replay cache swept its entire dictionary on
every request.

The costs are real and accepted. Rotation is net-new for this repository, so it carries
the risk of any first implementation and is covered by explicit tests for the overlap
window and for revocation taking effect immediately. Best-effort delivery means a
subscriber cannot treat this as an audit log — the gap-detectable design tells a consumer
when it has missed something, but recovering the missed events is the consumer's problem.
The strict no-inference rule for owner keys means a host that cannot resolve an owner gets
no delivery at all, which is the intended failure direction.

The allow-list projection has an ongoing maintenance cost that is easy to under-price:
every new payload field must be added to its event type's allow-list or it will not reach
subscribers, and the failure is silent at the producer. That is the trade accepted above —
the alternative fails silently at the *subscriber's* expense rather than the producer's,
and only one of those two silences is recoverable after the fact. It does mean the
projection tables belong next to the payload definitions and must be reviewed together;
a golden-JSON test per event type is what makes the omission visible at authoring time
rather than in production.

Making secrets write-only removes a support affordance: an operator who has lost a
subscriber's secret cannot look it up, and the only remedy is a rotation the subscriber
must be able to receive. That is the intended direction, but it means rotation has to be
genuinely operable rather than a rarely-exercised path, which is why the overlap window and
immediate revocation carry explicit tests rather than being assumed to work.

The infrastructure library targets `net9.0` only and already carries a framework reference
to ASP.NET Core, so hosting these endpoints there introduces no new dependency and keeps
ASP.NET out of the core libraries. The dual-target requirement continues to bind
`LmLifecycle` and `LmCore`, not this runtime.
