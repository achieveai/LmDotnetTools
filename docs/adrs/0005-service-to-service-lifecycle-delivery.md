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

Every event is filtered against the subscriber's owner key **before** delivery numbering,
which is what makes each subscriber's `delivery_sequence` contiguous and its loss
detection specific to itself (see [ADR 0002](0002-lifecycle-event-wire-contract.md)).

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

The control plane is deliberately minimal: register, rotate, revoke. There is no broad
subscription inspection API.

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

The infrastructure library targets `net9.0` only and already carries a framework reference
to ASP.NET Core, so hosting these endpoints there introduces no new dependency and keeps
ASP.NET out of the core libraries. The dual-target requirement continues to bind
`LmLifecycle` and `LmCore`, not this runtime.
