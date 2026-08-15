# Egress Policy Refresh and Remote-Safe Egress Auth Management

**Status:** Approved design
**Date:** 2026-08-13
**Tracking issue:** #286

## Problem

Predefined Egress Auth entries serve two separate purposes:

1. They contribute destination hosts and credential-provider references to a sandbox's `network.rules` and `auth_providers` during sandbox creation.
2. They provide current credential values to the authenticated gateway webhook when a request needs authorization.

The first part is immutable for the life of a sandbox. Adding, changing, or deleting a host does not update an existing sandbox. The second part is resolved at request time, so replacing credentials for an already-authorized host can take effect without rebuilding network policy.

The management API is currently designed for local use and relies on a network-location check rather than authenticated administrator identity. `EgressKeysController.RejectNonLoopback` inspects `Connection.RemoteIpAddress` and rejects any request carrying a forwarding header. That boundary is not sufficient for browser-based or remotely exposed administration, and it is not authorization. Detailed reproduction mechanics are intentionally omitted until the authorization and browser-isolation controls described here are implemented.

The management UI must work from any legitimate URL where the application is exposed. Existing and submitted credentials must never be returned to the browser.

## Goals

1. Apply host-policy additions, changes, and deletions to active workspaces without requiring a manual conversation restart.
2. Avoid interrupting an active run or creating a no-session window.
3. Allow Egress Auth management from any deployment hostname through authenticated, policy-authorized, same-origin requests.
4. Preserve a strict write-only secret boundary.
5. Prevent a metadata edit from silently moving an existing credential to a different host or a different token endpoint.
6. Provide local verification at every layer before deployment.

## Non-goals

- Add a gateway API for mutating a running sandbox's network policy.
- Return masked, partial, encrypted, or recoverable credential values to the browser.
- Change the gateway auth webhook's per-session-secret boundary.
- Implement a general-purpose secrets manager.
- Make wildcard cross-origin administration possible.
- Support multi-instance (scaled-out) deployment of the management surface. See section 0.

# 0. Deployment Scope and Topology Constraint

This design targets a **single application instance** that owns the egress-key store. That constraint is what makes an in-file revision, a file-backed admin-session store, and a process-local persist gate sound.

The constraint is enforced, not assumed:

- On startup, the process acquires an **exclusive advisory lock file** in the egress token directory (alongside `predefined-keys.json`). If the lock is already held, startup fails closed with a clear message instead of racing a second writer over the same snapshot.
- The lock is released on graceful shutdown; a stale lock from a crashed process is detected by recorded process identity and start time, not by age alone.

Running a second instance against the same store is unsupported. A future scaled-out design would need a shared transactional store and a distributed lock; it is explicitly out of scope here.

**At-rest protection.** `predefined-keys.json` and the OAuth token store hold credential material in plaintext JSON under a gitignored directory. This design requires:

- the directory and both files are created with owner-only permissions (`FileMode 0600` / directory `0700` on POSIX; owner-only DACL with inheritance disabled on Windows);
- a startup check that logs a warning and, when remote administration is enabled, fails closed if the permissions are broader than owner-only;
- deployment documentation stating that anyone with read access to that directory holds every configured credential.

Encrypting the store at rest is out of scope; the permission requirement and the single-instance constraint are the compensating controls.

# 1. Policy and Credential Lifecycle

## 1.1 Durable policy revision

`PredefinedKeyRegistry` owns a **durable, monotonically increasing policy revision**. The revision is persisted with the entry snapshot rather than living only in process memory, so a restart does not reset it and cannot make a session created under an older policy look current.

The persisted file changes from a bare `List<PredefinedKeyEntry>` to an envelope:

```json
{ "revision": 7, "entries": [ /* PredefinedKeyEntry[] */ ] }
```

Load is backward compatible: a legacy top-level array is read as `revision: 0` and rewritten in envelope form on the next mutation.

The revision advances **only** when an operation changes what the sandbox create payload would contain. `BuildAuthProviders` consumes exactly three fields per entry:

- `entry.Id` — the provider id, the webhook endpoint path, and the network rule id;
- `entry.Host` — the network rule's `hosts`;
- `entry.Kind` — only through `cacheTtlSeconds` (30s for custom headers, 300s otherwise).

Nothing else reaches the sandbox. Token endpoint, client id, client secret, refresh token, scopes, and header names **and** values are dynamic credential-provider configuration resolved by the gateway webhook per request; they are absent from the create payload entirely.

The revision therefore advances on, and only on:

- creating an entry;
- deleting an entry;
- changing an entry's host;
- changing an entry's kind.

An entry's id is stable for its lifetime, so provider identity changes only via create/delete and needs no separate rule. Every other write — token endpoint, client id, secrets, scopes, header names, header values — is credential-provider configuration and does **not** advance the revision or force sandbox replacement. Over-approximating here would replace live sandboxes for changes the sandbox cannot observe, which contradicts goal 2.

Rotation still takes effect promptly without replacement: the gateway resolves credential values per request within the provider cache TTL above.

Ordering and atomicity:

- The revision and the entry array are written in the **same** `AtomicJsonFile.WriteAsync` call, so a reader never observes a new revision with old entries or the reverse.
- The in-memory revision is published **only after** the durable write succeeds. This preserves the existing persist-first discipline in `UpsertAsync`/`RemoveAsync`, where the candidate set is written before the in-memory swap.
- Mutations remain serialized under the existing process-local `_persistGate`.
- A failed or cancelled persist publishes nothing: neither the entries nor the revision advance.

Each cached sandbox session records the revision used to create it.

## 1.2 Per-entry concurrency version

The policy revision answers "must live sandboxes be replaced?". It is the wrong token for "did someone else edit this entry while I was editing it?", because the majority of writes — every credential rotation and display-only edit — deliberately do not advance it. Two concurrent rotations checked against the revision would both pass, and one would be silently lost.

Each entry therefore carries its **own** monotonically increasing `version`, independent of the policy revision and persisted in the entry record.

The version travels as a **strong `ETag` on single-entry resources only**. A collection response cannot carry a per-item validator, so:

- `GET /keys/{id}` returns the entry and an `ETag: "<version>"` response header. This is the resource a client loads before editing.
- `GET /keys` returns each item's `version` as an ordinary metadata field in the body. The list response carries **no** `ETag`, and a version read from the list is not a validator — a client that wants to write re-reads the single-entry resource first.
- Every write — `PATCH`, `PUT .../credential`, `PUT .../configuration`, `DELETE .../refresh-token`, and `DELETE` — **must** carry the expected version in an `If-Match` request header. A write with no `If-Match` is rejected with `428 Precondition Required`; the client never gets to blind-write. `If-Match: *` is rejected the same way, because it asserts only existence.
- The write is applied only if the stored version matches. On mismatch the operation is rejected with `412 Precondition Failed` and a body naming the current version; nothing is persisted and no secret is echoed.
- On success the entry's `version` increments exactly once, in the same durable write as the entry change.
- **Every successful write that leaves the entry in existence returns the new version in a strong `ETag` response header**, so an operator making consecutive edits never has to re-read to obtain a fresh validator and can never submit a stale one. A bare `204` with no `ETag` is not an acceptable response for such a write.
- The two counters move independently: a credential rotation bumps `version` and leaves the revision untouched; a host change bumps both; a delete bumps the revision and retires the entry's version with it.
- Version checks are enforced under the same `_persistGate` as the mutation, so the compare and the write cannot interleave.

`DELETE /keys/{id}` is the one write with no successor version: it returns `204` with no `ETag`, because the resource no longer exists.

Creating an entry needs no `If-Match`; `POST /keys` returns `201` with a `Location` header and an `ETag` of `"1"`.

## 1.3 Partition lease and dispatch barrier

Policy replacement is only safe when nothing is actively using the predecessor. The current registry has no such notion: `_sessions` is a `ConcurrentDictionary<(string WorkspaceId, string AppId), Lazy<Task<SandboxSession>>>` with lazy single-flight create-or-get and no refcount over active users. A second conversation on the same partition can begin a turn while a replacement is retiring the session it just acquired.

This design adds a **partition-scoped lease** keyed by the same `(WorkspaceId, AppId)` tuple the session cache uses.

Lease state per partition:

- `activeLeases` — count of in-flight runs currently bound to the partition's session;
- `boundSessionId` — the session those leases were issued against;
- `sealed` — when set, no new lease may be issued against `boundSessionId`;
- a completion signal raised when `activeLeases` reaches zero.

Rules:

1. **Acquire before dispatch.** Every new turn acquires a lease before it obtains a session, and holds it for the whole run. The lease records the session id it was issued against.
2. **Seal before drain.** A refresh first sets `sealed`, which prevents new predecessor-bound leases. Callers arriving while sealed wait for the refresh rather than joining the predecessor.
3. **Drain.** The refresh waits for `activeLeases` to reach zero, bounded by a configured drain timeout.
4. **Swap under seal.** Successor creation and the binding compare-and-swap complete while the partition is sealed.
5. **Release and retire.** The seal is lifted with `boundSessionId` pointing at the successor; new leases attach to it. The predecessor is retired only after every predecessor lease has been released.

Leases are released in a `finally` so an aborted or faulted run cannot pin a partition permanently. If the drain timeout elapses, the refresh **abandons this attempt**, lifts the seal with the predecessor still bound, and reports `pending`; the next dispatch boundary retries. A timed-out drain never forces a mid-turn replacement.

## 1.4 Registry operations for safe replacement

`SandboxSessionRegistry` today exposes lazy create-or-get and an eviction path. It has no detached creation, no compare-and-swap, and no explicit candidate cleanup. Its existing replacement metadata (`_replacedSessions`, populated by `InvalidateSessionAsync`) records a predecessor id for gateway-404 recovery only, and that path **evicts before creating**, which is precisely the no-session window this design must not reproduce.

Add these concrete operations:

- `CreateCandidateSessionAsync(WorkspaceRef workspaceRef, SandboxCredential? credential, long revision, CancellationToken ct)` — creates a sandbox session from the current registry snapshot and returns it **detached**: it is registered in the per-session-id collections but the `(WorkspaceId, AppId)` binding is untouched. Nothing routes to it yet.
- `TryCommitSuccessorAsync((string WorkspaceId, string AppId) key, string expectedPredecessorSessionId, SandboxSession candidate)` — atomically replaces `_sessions[key]` **only if** the current entry is a completed `Lazy<Task<SandboxSession>>` whose result is the expected predecessor. It installs a pre-completed `Lazy` so no caller can observe a creating state, and it uses the same identity discipline already applied in `InvalidateSessionAsync` — compare the exact entry we own, never a blind key overwrite that could clobber a concurrently installed session. Returns `false` on a lost race.
- `DiscardCandidateAsync(SandboxSession candidate)` — issues the gateway DELETE and funnels through the existing `EvictSessionStateAsync` so every per-session-id collection is cleared. Used when creation succeeded but the swap did not.
- `RetirePredecessorAsync(SandboxSession predecessor)` — retires the swapped-out session after its leases drain, recording predecessor/successor linkage through the existing replacement metadata.

`GetOrCreateLiveSessionAsync` keeps its current contract. It is deliberately **not** a replacement boundary: `FileBrowserController.ResolveSessionAsync`, `ConversationTranscriptWriter.ResolveSessionAsync`, and the shutdown-cleanup call in `Program.cs` all acquire a session while a larger run may already be active, and none of them may trigger replacement.

## 1.5 Safe replacement algorithm

One shared operation, `EnsurePolicyCurrentBeforeTurnAsync(WorkspaceRef workspaceRef, SandboxCredential? credential, CancellationToken ct)`, is the single entry point. It compares the cached session's recorded revision with the registry revision and, when stale, runs:

1. Seal the partition and drain active leases (section 1.3). On timeout, leave the predecessor bound and report `pending`.
2. Keep the predecessor session available throughout.
3. `CreateCandidateSessionAsync` from the current registry snapshot.
4. Verify creation succeeded. On failure, keep the predecessor bound, lift the seal, and report `failed`.
5. `TryCommitSuccessorAsync` against the expected predecessor.
6. On a lost compare-and-swap, `DiscardCandidateAsync` and keep the winning binding.
7. On success, record predecessor/successor linkage, lift the seal onto the successor, and `RetirePredecessorAsync`.

The predecessor binding is never removed before a successor exists.

## 1.6 Dispatch boundaries

`EnsurePolicyCurrentBeforeTurnAsync` is invoked immediately before every new run, on exactly these paths:

- the interactive **WebSocket** path, before starting a turn;
- the **REST message** path, before starting a turn;
- the **S2S** message path, before starting a turn;
- the **daemon run-provisioning** path in `ReviewSessionProvisioner`, before provisioning a review run.

A stale-revision check that only logs, reports, or closes a session does not satisfy this design; each path must call the shared operation and must await its outcome before dispatch.

The non-boundary call sites listed in section 1.4 must not call it. A structural test enumerates session-acquiring call sites and asserts the boundary/non-boundary split, so a newly added dispatch path cannot silently skip the refresh.

## 1.7 Active-run rule

Policy replacement occurs before dispatching a new turn, not in the middle of an active turn. Existing active runs finish against their established sandbox, protected by their leases. The next turn observes the new policy.

A host deletion also advances the revision. The webhook will stop returning credentials immediately, but the old sandbox retains its network allow rule until replacement; replacement closes that residual network reachability.

# 2. Remote Management Security

## 2.1 Deployment surface

The application currently applies a **wildcard CORS policy**: `LmStreamingOptions.AllowedOrigins` defaults to `["*"]` and `UseLmStreaming()` translates that into `AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()`. `AllowedHosts` is `"*"`. There is no forwarded-headers, HTTPS-redirection, authentication, authorization, antiforgery, or rate-limiting middleware anywhere in the pipeline. Every item below is therefore new work, not documentation of an existing control.

**CORS.** Documenting `[DisableCors]` is not sufficient by itself. Whether an endpoint-level override actually takes effect depends on where `UseCors` sits relative to endpoint routing, and the sample never calls `UseRouting()` explicitly — routing is inserted implicitly by `MapControllers`. The effective ordering is therefore incidental rather than declared, and this design does not assert that the current pipeline fails; it requires that the ordering stop being incidental so the behavior is intentional and directly testable. Three requirements, all of which must hold:

1. Call `UseRouting()` explicitly before `UseLmStreaming()`, so CORS is unambiguously endpoint-aware and endpoint metadata is available to override the default policy. This makes the guarantee explicit instead of relying on implicit middleware insertion that a future edit could silently change.
2. Apply a named `EgressAuthNoCors` policy (empty origin set) to the management route group, and prove by test that a cross-origin preflight and a cross-origin actual request to a management route receive **no** `Access-Control-Allow-Origin` header.
3. Fail closed at startup: when remote administration is enabled, reject a configuration whose `AllowedOrigins` contains `*`. If the endpoint-level override cannot be proven for a given hosting configuration, this check is the backstop — a wildcard global policy and remote administration are never simultaneously permitted.

**Hosts and proxies.**

- `AllowedHosts` must enumerate the deployment's hostnames. `"*"` is rejected at startup when remote administration is enabled.
- `UseForwardedHeaders` is registered **first** in the pipeline, ahead of request logging, with `KnownProxies`/`KnownNetworks` populated from deployment configuration. The default empty known-proxy set means forwarded headers are ignored, which is the correct fail-closed default.
- `UseHttpsRedirection` plus HSTS are enabled whenever remote administration is enabled; non-loopback access over plain HTTP is refused.
- A spoofed `Host` or `X-Forwarded-*` from an unknown proxy must neither grant access nor alter the scheme or client identity used for logging and policy decisions.

**Pipeline order.** The resulting order is:

```
UseForwardedHeaders
UseSerilogRequestLogging
UseHttpsRedirection            (remote administration enabled)
[dev] UseViteDevelopmentServer
UseStaticFiles
UseRouting
UseLmStreaming()               (UseWebSockets + endpoint-aware UseCors)
Map("/ws"), Map("/ws/subagent")
UseAuthentication
UseAuthorization
MapControllers
fallback
```

The `/ws` branches short-circuit ahead of authentication and keep their existing boundary unchanged.

## 2.2 Admin authentication scheme

The sample adds a small server-managed admin session rather than exposing a reusable management secret to browser JavaScript.

- **Scheme.** A named cookie scheme `EgressAuthAdmin`, registered with `AddAuthentication().AddCookie("EgressAuthAdmin", ...)`. It is not the default scheme; nothing else in the application is affected.
- **Cookie.** Name `__Host-EgressAuthAdmin`, `HttpOnly`, `Secure`, `SameSite=Strict`, `Path=/`, no `Domain`. The loopback-HTTP development profile may fall back to an unprefixed name only while remote administration is disabled.
- **No redirects.** `Events.OnRedirectToLogin` returns `401` and `OnRedirectToAccessDenied` returns `403` for API routes instead of issuing a 302 to an HTML login page.
- **Installation secret.** One `EgressAuthAdmin` secret is supplied from an environment variable or external secret store. It is never present in checked-in configuration and is never returned by any endpoint. Configuration stores a salted hash, not the secret.
- **Constant-time verification.** The unlock endpoint compares the submitted value using `CryptographicOperations.FixedTimeEquals` over fixed-length hashes. A principal is constructed **only after** that comparison succeeds; no partially-authenticated principal exists at any point.
- **Claims.** On success the principal carries exactly: `ClaimTypes.NameIdentifier` = a freshly generated admin-session id, `egress_auth_admin` = `true`, and an issued-at claim. No credential material is placed in a claim.
- **Lifetimes.** Bounded idle timeout (sliding) and a hard absolute expiry, both configured; the absolute expiry is enforced from the issued-at claim and is not extendable by activity.
- **Rate limiting.** The unlock endpoint is rate-limited per client identity and globally, and emits only success/failure audit metadata — never the submitted value or a prefix of it.
- **Fail closed.** When the installation secret is absent, remote administration is disabled and every management route returns `503`; it does not silently fall back to the loopback check.
- **Loopback check.** The existing `RejectNonLoopback` behavior may remain as an optional development restriction layered *on top of* authentication. It is not authorization and must not be the only gate.

## 2.3 Authorization policy and route isolation

- **Policy.** `EgressAuthAdmin` = `RequireAuthenticatedUser()` + `RequireClaim("egress_auth_admin", "true")` + `AddAuthenticationSchemes("EgressAuthAdmin")`. Restricting the scheme prevents any future scheme from satisfying the policy by accident.
- **Route group.** Management endpoints move to a dedicated controller under `api/auth/egress-admin`, with the policy applied structurally to the controller so every current and future action inherits it.
- **Anonymous allowlist.** Exactly two endpoints on that controller carry `[AllowAnonymous]`, because both must be reachable before an admin session can exist:
  - `GET /api/auth/egress-admin/antiforgery` — the token bootstrap; unlock itself requires a token.
  - `POST /api/auth/egress-admin/unlock` — the operation that establishes the session.

  This allowlist is closed. No third endpoint may be anonymous, and neither of these two reads, returns, or mutates entry state. Both remain fully subject to the `EgressAuthNoCors` policy, antiforgery validation, and rate limiting — dropping the admin policy does not drop any other control.
- **Webhook isolation.** `AuthWebhookController` at `api/auth/webhook/{provider}` is explicitly left on its gateway per-session-secret boundary and is explicitly marked to not participate in the admin policy. The two mechanisms are never conflated, and no broad `api/auth` route-group policy is introduced.
- **Legacy surface retired.** The existing `EgressKeysController` at `api/auth/egress-keys` — including its `GET`, `POST` upsert, and `DELETE {id}` actions and its `RejectNonLoopback` gate — is **deleted**, not left in place alongside the new controller. Leaving it mounted would preserve an unauthenticated registry-mutating route that bypasses every control in this design.
- **Structural tests.** Tests enumerate `EndpointDataSource` and assert:
  1. every endpoint under `api/auth/egress-admin` carries the `EgressAuthAdmin` policy **except** the exact two-route anonymous allowlist above — asserted as an exact set, so adding a third anonymous endpoint fails the test;
  2. the gateway webhook route does not carry the admin policy;
  3. no endpoint anywhere in the application mutates the `PredefinedKeyRegistry` outside `api/auth/egress-admin`, and no route matching `api/auth/egress-keys` is mapped at all.

## 2.4 Antiforgery

Antiforgery is required for every state-changing management request, including unlock and logout. Because no antiforgery infrastructure exists today and the current client sends `POST`/`DELETE` with no token, both the server contract and the client must be built.

- **Configuration.** `AddAntiforgery` with header name `X-Egress-Auth-CSRF` and cookie name `__Host-EgressAuthCsrf` (`Secure`, `SameSite=Strict`, `Path=/`, no `Domain`).
- **Enforcement.** `[AutoValidateAntiforgeryToken]` on the admin controller, so every non-`GET`/`HEAD`/`OPTIONS`/`TRACE` action validates — unlock and logout included. Individual actions do not opt in one at a time.
- **Bootstrap.** `GET /api/auth/egress-admin/antiforgery` is same-origin and one of the two `[AllowAnonymous]` endpoints (section 2.3), because unlock itself needs a token. It sets the antiforgery cookie and returns **only** the request token in its body. It carries no entry metadata and no credential material, is covered by the `EgressAuthNoCors` policy, and is rate-limited.
- **Client contract.** The bundled client fetches a token at load, sends it in `X-Egress-Auth-CSRF` on every unlock, logout, `POST`, `PUT`, `PATCH`, and `DELETE`, and refetches once on an antiforgery rejection before surfacing an error. No token is stored outside memory.
- **Failure response.** An antiforgery failure returns `400` with a generic code and no entry metadata.

Origin validation may be added as defense in depth. It is not a substitute for authentication or antiforgery.

## 2.5 Admin session store and revocation

An encrypted self-contained cookie enforces expiry but cannot by itself revoke a specific session. This design adds a **server-side admin-session store**.

- The store is file-backed in the egress token directory, under the same single-instance lock and owner-only permissions as the key store. Each record holds the admin-session id, issued-at, last-seen, absolute expiry, and a revoked flag. It contains no credential material.
- `CookieAuthenticationEvents.OnValidatePrincipal` looks the session up on every request and rejects the principal when the record is missing, revoked, idle-expired, or past absolute expiry; rejection signs the cookie out rather than merely returning `401` once.
- Logout marks the record revoked and deletes the cookie; the revoked record is retained until its absolute expiry so a replayed cookie stays rejected.
- **Data Protection.** Keys are persisted to the egress token directory with `SetApplicationName`, owner-only permissions, and the default key lifetime; rotation semantics are documented. Because the deployment is single-instance (section 0), no shared key ring is required. A restart with an intact key ring keeps admin sessions valid; a lost or rotated-out key ring invalidates them, which is an acceptable fail-closed outcome.

## 2.6 Local bootstrap

For local testing, the admin secret is supplied through a process environment variable. The UI is opened from the sample's own origin, the client fetches an antiforgery token, the operator unlocks the session, and all subsequent metadata and mutation calls use the HttpOnly cookie plus the antiforgery header. The browser does not persist the entered secret and clears the input after submission. No credential is placed in browser local storage, session storage, a URL, or client-side configuration.

# 3. Secret-Safe API Contract

## 3.1 Route surface

All management routes live under `api/auth/egress-admin`:

| Route | Verb | Auth | Purpose |
| --- | --- | --- | --- |
| `/antiforgery` | GET | Anonymous | Antiforgery token bootstrap |
| `/unlock` | POST | Anonymous | Establish admin session |
| `/logout` | POST | `EgressAuthAdmin` | Revoke admin session |
| `/keys` | GET | `EgressAuthAdmin` | List entry metadata |
| `/keys/{id}` | GET | `EgressAuthAdmin` | Single entry; the validator source for edits |
| `/keys` | POST | `EgressAuthAdmin` | Create an entry (complete write-only shape) |
| `/keys/{id}` | PATCH | `EgressAuthAdmin` | Display-only metadata |
| `/keys/{id}/credential` | PUT | `EgressAuthAdmin` | Credential-only rotation |
| `/keys/{id}/configuration` | PUT | `EgressAuthAdmin` | Complete routing + credential replacement |
| `/keys/{id}/refresh-token` | DELETE | `EgressAuthAdmin` | Clear an optional refresh token |
| `/keys/{id}` | DELETE | `EgressAuthAdmin` | Delete the entry |

Request and response headers for the entry resources:

| Operation | Request | Success response |
| --- | --- | --- |
| `GET /keys` | — | `200`, no `ETag`; each item carries `version` in the body |
| `GET /keys/{id}` | — | `200` + `ETag: "<version>"` |
| `POST /keys` | — | `201` + `Location` + `ETag: "1"` |
| `PATCH /keys/{id}` | `If-Match: "<version>"` | `200` + `ETag: "<newVersion>"` |
| `PUT /keys/{id}/credential` | `If-Match: "<version>"` | `200` + `ETag: "<newVersion>"` |
| `PUT /keys/{id}/configuration` | `If-Match: "<version>"` | `200` + `ETag: "<newVersion>"` |
| `DELETE /keys/{id}/refresh-token` | `If-Match: "<version>"` | `200` + `ETag: "<newVersion>"` |
| `DELETE /keys/{id}` | `If-Match: "<version>"` | `204`, no `ETag` (resource is gone) |

A missing or `*` `If-Match` on any write is `428`; a mismatch is `412`. Success bodies are metadata only and never carry a secret.

The two anonymous rows are the closed allowlist from section 2.3; every other row carries the policy. All rows, anonymous included, carry `EgressAuthNoCors`, antiforgery validation on state-changing verbs, and rate limiting.

The gateway-facing `api/auth/webhook/{provider}` route is unchanged. The legacy `api/auth/egress-keys` routes are deleted.

## 3.2 Read model

The read DTO contains metadata only:

- entry ID and entry `version` (a body field on every response; additionally a strong `ETag` header on single-entry responses per section 1.2);
- display name, host, and kind;
- configured header names;
- booleans such as `hasClientSecret` and `hasRefreshToken`;
- token endpoint, client id, and scopes (non-secret routing metadata);
- timestamps, policy revision, and policy-refresh state.

It has no fields capable of carrying header values, API keys, client secrets, refresh tokens, or reusable masked placeholders. The existing `EgressKeyView` already satisfies this and keeps that property in its relocated form (section 3.3). Keep a reflection-based test that rejects secret-bearing properties on the response type.

## 3.3 Operation matrix

The three write contracts are disjoint. There is no single broad upsert, and there is no global "blank means preserve" rule.

| | `PATCH /keys/{id}` | `PUT /keys/{id}/credential` | `PUT /keys/{id}/configuration` |
| --- | --- | --- | --- |
| **Editable fields** | `displayName`, `description` only | secret values only | all routing metadata **and** all required credentials |
| **Accepts secrets** | No — a secret field present is `400` | Yes, required | Yes, required |
| **Changes routing** | No | No | Yes |
| **Blank / omitted secret** | n/a | `400 credential_required` | `400 credential_required` |
| **Expected entry version** | `If-Match` required | `If-Match` required | `If-Match` required |
| **Bumps entry version** | Yes | Yes | Yes |
| **Advances policy revision** | No | No | Only if host or kind changed |

Every row of the version/revision distinction follows section 1.2 and section 1.1: the entry version guards concurrent edits and always moves; the policy revision governs sandbox replacement and moves only for host or kind. A configuration replacement that rewrites the token endpoint, client id, scopes, or header set but leaves host and kind alone bumps the version and does **not** replace any sandbox.

Rules:

- **Metadata (`PATCH`).** The editable set is exactly `displayName` and `description`. A routing- or authentication-sensitive field — host, kind, token endpoint, client id, header-name set, scopes — is rejected with `409 credential_replacement_required`. A secret-bearing field is rejected with `400`, because this endpoint accepts no secrets at all. This endpoint preserves the stored secret precisely because it cannot move it anywhere.
- **Credential rotation (`PUT .../credential`).** Accepts only new secret values for the entry's existing kind, and only for an unchanged policy shape. A blank or omitted value is a validation error, never "leave unchanged". It cannot alter host, kind, token endpoint, client id, or header names.
- **Complete replacement (`PUT .../configuration`).** Atomically replaces all routing-sensitive metadata **and** all required credentials. Every required credential must be supplied non-blank; omission is `400`, never a merge. It never carries an existing credential into a new host or endpoint. Creating an entry via `POST /keys` uses the same complete write-only request shape.
- If a complete replacement value is unavailable to the operator, the correct action is **delete and recreate**, not silent reuse of the stored secret.

Every management write operation returns metadata only, plus the strong `ETag` carrying the new version (section 1.2). A bare `204` with no `ETag` is not acceptable for any write except `DELETE /keys/{id}`, where no successor version exists. A submitted value is never placed in:

- response DTOs;
- structured log properties;
- exception messages;
- audit records;
- model-state serialization;
- browser-readable configuration.

**Behavior this replaces.** The current `POST /api/auth/egress-keys` upsert violates the invariant in three specific places, all of which are removed:

- `BuildCustomHeadersEntry` returns `existing with { Id = id, Host = host }` when the header list is omitted, so changing only the host carries every stored header value to the new destination.
- `BuildCustomHeadersEntry` resolves a blank value on an existing header name to the stored secret, independent of whether the host changed.
- `BuildOAuthEntry` applies `Coalesce(request.X, existing?.X)` to `TokenEndpoint`, `ClientId`, `ClientSecret`, and `RefreshToken`, so a caller can change the token endpoint while the old client secret or refresh token is preserved and subsequently posted to the new endpoint.

**Contract types move out of the controller.** `EgressKeyRequest`, `EgressHeaderInput`, and `EgressKeyView` are currently declared as `public sealed record` at the bottom of `EgressKeysController.cs`, in namespace `AchieveAi.LmDotnetTools.LmAgentInfra.Controllers`. Deleting that controller cannot take the contract types with it, so they move to dedicated files under a contracts folder — one type per file — alongside the new request shapes this design adds (metadata patch, credential rotation, complete configuration).

- The **secret-free view remains a hard requirement**. `EgressKeyView` (or its renamed replacement) gains `version` and the policy-refresh state and gains nothing capable of carrying a header value, API key, client secret, or refresh token. The existing reflection assertion in `tests/LmStreaming.Sample.Tests/Auth/EgressKeysControllerTests.cs`, which asserts the view exposes no secret-bearing property names, moves with the type and must keep passing — it is the structural guard behind the write-only boundary, not an incidental test.
- `EgressKeyRequest` and `EgressHeaderInput` are superseded by the three disjoint request shapes in section 3.3. `EgressKeyRequest` in particular cannot survive as-is: its single broad shape is what made the preserve-on-blank merge expressible.

**Package API break — called out explicitly.** `src/LmAgentInfra` ships as the NuGet package `AchieveAi.LmDotnetTools.LmAgentInfra`, so these three `public` records are part of its published surface. Removing `EgressKeysController` and relocating or replacing the records is a **breaking change** for any external consumer, both source and binary. This design accepts that break rather than preserving a type whose shape encodes the vulnerability.

- Preserve compatibility where it is free: keep the `AchieveAi.LmDotnetTools.LmAgentInfra` namespace root and keep `EgressKeyView`'s existing property names and types, adding only new members, so a consumer that merely reads the view needs at most a `using` change.
- Do not preserve `EgressKeyRequest`'s shape or the `api/auth/egress-keys` route. A type-forwarding or `[Obsolete]` shim is explicitly rejected here: it would keep the unsafe upsert contract expressible and callable, which defeats the purpose of deleting it.
- Record the break in `CHANGELOG.md` under a breaking-changes heading, naming the removed controller, the removed route, each moved or removed type, and the replacement endpoint for each.
- The only in-repo consumers are `tests/LmStreaming.Sample.Tests/Auth/EgressKeysControllerTests.cs` and the sample's client; both are updated in the same change, so the break is contained within the repository at merge time.

## 3.4 Token endpoint trust

The current validation accepts **any** absolute HTTPS URL. Combined with preserve-on-blank, that lets an operator or a request forgery point an existing secret at an attacker-chosen endpoint. Two controls, both required:

1. **Allowlist.** Token endpoints are restricted to an explicitly configured allowlist of trusted origins. An endpoint outside the allowlist is rejected with `400 token_endpoint_not_allowed`. An empty allowlist means no OAuth entries can be created, which is the correct fail-closed default.
2. **Fresh credentials on identity change.** Any change to the token endpoint, provider identity, client id, or kind is routing-sensitive and can only be made through complete configuration replacement, which requires a full fresh credential set.

A captured-outbound-request test asserts that after an endpoint change, the request observed at the new endpoint carries only the newly supplied secret, and that the previous secret canary appears in no outbound request.

## 3.5 Clear and delete

OAuth entries may expose an explicit clear-optional-refresh-token operation where the resulting entry remains valid. Required credentials cannot be cleared while retaining an unusable entry; delete the entry instead.

Custom-header entries have no clear-all operation because every configured header requires a non-empty value. Individual removal is performed through complete configuration replacement, which supplies the full remaining header-name/value set.

**Durable deletion.** Deleting an entry removes the definition and its persisted minted token. The current `TryRemovePersistedTokenAsync` is best-effort and logs a failure while still reporting success, which can leave a reusable plaintext access or refresh token on disk indefinitely. That is replaced by:

- a durable **deletion tombstone** written with the entry removal, naming the token-store key that still needs cleanup;
- a retry that runs on a bounded schedule and at startup until the token is gone;
- an unreconciled tombstone older than a configured threshold raises an operational alert and surfaces in the entry list as a `cleanup_pending` state;
- the delete response reports success only for the definition removal; outstanding token cleanup is reported explicitly rather than silently swallowed.

Deleting an entry advances the policy revision. Clear and delete actions have confirmation in the UI.

## 3.6 Policy refresh states

Metadata responses expose one of these states:

- `current`: all cached partitions use the current policy revision;
- `pending`: at least one partition is stale but is waiting for a safe boundary, or a drain timed out and will retry;
- `refreshing`: successor creation or swap is in progress;
- `failed`: the predecessor remains bound after a refresh failure; a later boundary may retry;
- `cleanup_pending`: the definition is deleted but a token tombstone is unreconciled.

The state contains no session secret, auth token, or credential material.

## 3.7 Audit events

Audit actor (admin-session id), action, entry ID, host, kind, timestamp, policy revision, and affected session count. Never log or destructure a secret-bearing request object. Unlock and logout emit success/failure metadata only.

# 4. Client Behavior

The modal lists metadata and configured/not-configured indicators only.

Editing a secret starts with an empty input. The browser never receives the current value, and the client never sends a masked sentinel as if it were a credential.

The client chooses the endpoint by what changed:

- display-only edits use `PATCH`;
- a secret change with no routing change uses the credential endpoint and requires a non-empty value;
- any host, kind, token endpoint, client id, header-name-set, or scope change uses the configuration endpoint, and the form requires the full credential set before it will submit.

A `409 credential_replacement_required` from `PATCH` prompts the operator for the full credential set rather than retrying the metadata call.

The client loads `GET /keys/{id}` before an edit and sends that response's `ETag` as `If-Match` on the write. It does **not** derive a validator from the list response, which carries version as display metadata only. Each successful write returns a fresh `ETag`, which the client adopts immediately, so a sequence of edits on one entry needs no re-read between them.

A `412` means another administrator changed the entry: the client reloads, shows what changed, and requires the operator to re-confirm — it never silently retries with the new version, because that would reapply an edit formed against stale state. A `428` is a client bug, not an operator condition, and surfaces as such.

After a policy-shape change, the UI reports that active workspaces will refresh at their next safe turn. It does not claim immediate propagation before the replacement succeeds.

# 5. Error Handling

- Unauthorized (`401`), forbidden (`403`), and antiforgery-failure (`400`) responses reveal no entry metadata.
- Validation errors identify fields and constraints but never include submitted secret values.
- `409 credential_replacement_required` (wrong endpoint for the change), `428 Precondition Required` (no expected entry version supplied), and `412 Precondition Failed` (entry version mismatch) are distinct and separately actionable. A `412` body names the current version and echoes no secret.
- A failed successor creation leaves the predecessor bound and returns or records a retryable refresh state.
- A failed successor **sandbox** cleanup is best-effort and logged without secrets, matching the current lifecycle cleanup contract; a discarded candidate holds no credential material. This best-effort allowance does **not** extend to persisted-token cleanup, which is durable per section 3.5.
- A compare-and-swap loss deletes the unused candidate and uses the winning binding.
- A drain timeout is not an error: it leaves the predecessor bound and reports `pending`.
- A rejected write — validation, `412`, or `428` — advances neither the entry version nor the policy revision.
- Credential rotation failures advance neither counter. Successful credential rotation advances the entry version only.

# 6. Local-First Verification Plan

No deployment begins until this ladder passes locally.

## Phase A — unit tests

### Management response and update contract

1. Keep the reflection assertion that the response DTO exposes no secret-bearing fields. It moves with the view type to its new contract file and must still pass; a deleted-and-not-reinstated assertion reads exactly like a passing one, so the suite total is checked against the pre-move baseline.
2. Serialize every response shape and assert that known submitted secret canaries are absent.
3. Verify credential replacement returns metadata only plus an `ETag` carrying the new version, and never a bare `204`.
4. Verify `PATCH` rejects any secret field with `400`.
5. Verify `PATCH` rejects a host, kind, token endpoint, client id, header-name-set, or scope change with `409 credential_replacement_required`.
6. Verify credential rotation rejects a blank or omitted secret with `400 credential_required`.
7. Verify configuration replacement rejects an omitted required credential with `400`, and never merges a stored value.
8. **Regression:** change an entry's host through configuration replacement and assert the stored credential is the newly supplied one and the old credential canary is absent from the persisted entry.
9. **Regression:** attempt a host change through every other endpoint and assert none of them succeeds.
10. Verify explicit clear removes an optional refresh token and leaves the entry valid.
11. Verify a token endpoint outside the allowlist is rejected with `400 token_endpoint_not_allowed`.
12. Verify error messages and captured structured logs never contain secret canaries.

### Authentication and authorization

1. An unauthenticated request to every management action **except** `GET /antiforgery` and `POST /unlock` returns `401`, not a 302 redirect. The test derives the endpoint list from `EndpointDataSource` and subtracts the allowlist, so a newly added action is covered automatically.
2. `GET /antiforgery` and `POST /unlock` succeed without an admin session, and are the **only** endpoints that do.
3. An authenticated caller lacking the `egress_auth_admin` claim returns `403`.
4. An authorized caller succeeds from both loopback and a simulated non-loopback address.
5. A structural `EndpointDataSource` test asserts the anonymous set under `api/auth/egress-admin` equals exactly `{GET /antiforgery, POST /unlock}` — an added third anonymous endpoint fails — and that every remaining endpoint carries the policy.
6. A structural test asserts the gateway webhook route does not carry the admin policy.
7. A structural test asserts no route matching `api/auth/egress-keys` is mapped, and that no endpoint outside `api/auth/egress-admin` mutates the `PredefinedKeyRegistry`.
8. The two anonymous endpoints still enforce no-CORS, antiforgery (for `POST /unlock`), and rate limiting.
9. Unlock rejects a wrong secret, is rate-limited, and emits no secret material; the comparison path is constant-time over fixed-length hashes.
10. Remote mode fails closed with `503` when the installation secret is absent.
11. Startup fails closed when `AllowedOrigins` contains `*` or `AllowedHosts` is `"*"` while remote administration is enabled.
12. Startup fails closed when the single-instance lock is already held.

### Antiforgery

1. The bootstrap endpoint returns a token, sets the cookie, carries no entry metadata, and is reachable before unlock.
2. Unlock, logout, and every mutation fail with `400` when the token is missing.
3. The same set fails when the token is malformed or belongs to a different session.
4. The same set succeeds with a valid token.

### Admin session lifecycle

1. Idle timeout and absolute expiry each reject a previously valid cookie.
2. Logout revokes the session; replaying the pre-logout cookie fails.
3. A cookie whose session record is missing from the store fails validation.

### Policy revision

1. Create, delete, host change, and kind change each increment the revision.
2. **Negative:** token endpoint, client id, client secret, refresh token, scope, header-name, and header-value changes each leave the revision unchanged. This is the test that keeps the revision from over-approximating.
3. Assert directly against the sandbox payload builder that the fields in test 2 are absent from `BuildAuthProviders` output, so the negative rule is anchored to what the sandbox actually receives rather than to a hand-maintained list.
4. Failed persistence publishes neither entries nor an incremented revision.
5. The revision survives a registry restart, and a legacy top-level-array file loads as revision `0`.
6. Concurrent updates produce a monotonic, observable revision.

### Per-entry version

1. Every write endpoint rejects a request with no `If-Match` with `428`, and rejects `If-Match: *` with `428`.
2. Every write endpoint rejects a stale `If-Match` with `412`, persists nothing, and echoes no secret.
3. A matching `If-Match` succeeds and increments the entry version exactly once.
4. A credential rotation increments the entry version and leaves the policy revision unchanged.
5. A host change increments both the entry version and the policy revision.
6. Two concurrent rotations against the same starting version: exactly one succeeds and the other gets `412`. This is the lost-update case the policy revision alone could not catch.
7. `GET /keys/{id}` returns a strong `ETag` that `If-Match` accepts on the next write.
8. `GET /keys` returns no `ETag` header, and each list item carries `version` in the body.
9. **Consecutive edits:** every successful write returns an `ETag` whose value is the new version, and feeding that header straight into a second write succeeds with no intervening `GET`. This is the test that keeps a client from having to reuse a stale validator.
10. `DELETE /keys/{id}` returns `204` with no `ETag`; every other write returns a body plus an `ETag`, and none returns a bare `204`.
11. A created entry starts at version 1, needs no `If-Match`, and returns `201` with `Location` and `ETag: "1"`.

## Phase B — registry/session integration tests

Use a fake gateway that records sandbox-create and delete requests.

1. Create a revision-`N` session and assert its request contains the expected host rule.
2. Rotate only the credential and assert no replacement create occurs.
3. Change the token endpoint, client id, scopes, and header names through complete configuration replacement while leaving host and kind unchanged, and assert **no** replacement create occurs and the sandbox payload is byte-identical.
4. Add a host, begin the next turn, and assert a successor is created with revision `N+1` and both expected rules.
5. Assert the predecessor remains bound until the successor create succeeds.
6. Force successor creation failure and assert the predecessor remains usable and the state is `failed`.
7. Force a compare-and-swap loss and assert the unused candidate is deleted and the winning binding is used.
8. Force candidate-cleanup failure and assert it is logged, does not fail the operation, and does not disturb the winning binding.
9. Delete a host and assert the successor no longer contains its network rule.
10. **Barrier:** hold a lease for an in-flight run, request a refresh, and assert no replacement occurs until the lease is released.
11. **Barrier:** while sealed, assert a newly arriving turn does not attach to the predecessor.
12. **Barrier:** exceed the drain timeout and assert the predecessor stays bound, the seal is lifted, and the state is `pending`.
13. **Barrier:** fault a run mid-lease and assert the lease is released and the partition is not pinned.
14. Assert each `(workspaceId, callerAppId)` partition refreshes independently and retains credential isolation.
15. Verify replacement metadata identifies predecessor and successor correctly.
16. **Per-path:** for each of the WebSocket, REST, S2S, and daemon-provisioning paths, assert stale policy is refreshed at the safe boundary and that no replacement occurs mid-turn.
17. **Structural:** assert the non-boundary session-acquisition call sites do not trigger replacement.
18. **Outbound capture:** change an OAuth entry's token endpoint through complete replacement and assert the old secret canary appears in no outbound request to the new endpoint.

## Phase C — browser security tests

Run the sample locally and use Playwright through the repository's existing task-isolated testing workflow.

1. Open the legitimate UI from its actual origin and verify list/create/edit/delete work for an authorized user.
2. Run a repository-local browser-security regression fixture from an untrusted origin against every management route. Keep the fixture's transport details private until the authorization fix ships.
3. Assert every cross-origin read and mutation fails closed, and that no management response carries an `Access-Control-Allow-Origin` header.
4. Omit or corrupt the antiforgery token and assert every mutation, plus unlock and logout, fails.
5. Verify a valid antiforgery token succeeds.
6. Inspect browser network responses and assert no response contains the submitted secret canary.
7. Confirm the edit form starts empty and never renders an existing secret or masked reusable value.
8. Confirm the client selects `PATCH` / credential / configuration correctly, and that a `409 credential_replacement_required` prompts for the full credential set.
9. Repeat behind a local reverse proxy using configured trusted forwarded headers; verify an untrusted spoofed forwarded header does not grant access or alter scheme or client identity, and that a spoofed `Host` outside `AllowedHosts` is rejected.
10. Verify logout invalidates the session for subsequent browser requests.

## Phase D — real local sandbox test

Use the locally published LmStreaming.Sample, gateway, and egress proxy.

1. Start with a sandbox whose policy does not contain a local test HTTPS service host.
2. Invoke the installed sandbox-auth skill helper and prove the request is denied by policy.

   The helper is supplied by a mounted plugin marketplace, not authored by this repository, and its mount path is environment-specific — it appears under different marketplace roots in different installations. The test must therefore resolve it through the plugin discovery mechanism rather than a literal path:

   - enable the marketplace through the workspace's `Marketplaces` alias configuration (`WorkspaceRef.Marketplaces`, defaulting to `SandboxGatewayOptions.Marketplaces`);
   - invoke the skill by its plugin-qualified name (`sandbox-auth:egress-auth`) and reference its scripts through `${CLAUDE_PLUGIN_ROOT}` inside the sandbox;
   - allow an explicit configuration override for an installation that resolves the helper elsewhere.

   Keep the missing-dependency preflight: if the marketplace alias is not configured or the skill does not resolve, report the environment as not runnable rather than failing the phase. Do not hardcode a marketplace mount name.
3. Add the service through the legitimate UI without restarting the application or conversation.
4. Trigger the next turn and observe one successor sandbox creation.
5. Repeat the fetch and prove it reaches the service with the expected injected test credential.
6. Rotate the credential only and prove no sandbox replacement occurs; after the gateway cache TTL, the service receives the new credential.
7. Delete the entry, trigger the next turn, and prove the replacement policy no longer allows the host.
8. Verify application, gateway, proxy, browser, and structured test logs contain none of the secret canaries.
9. Capture the denied-then-allowed transcript as a **redacted, committed fixture** under `tests/fixtures/egress-policy-refresh/` recording the HTTP status, the `status` field, and the exit code for both the RED and GREEN observations. Credential values, hostnames of non-public services, and session identifiers are redacted. This fixture, together with issue #286, is the reviewable baseline for the acceptance criteria below.

Use a disposable local HTTPS echo service for Phases A-D. It must report only whether the expected credential matched, not print the credential value.

## Phase E — automated acceptance against a non-production endpoint

The reproducible incident baseline is the committed fixture from Phase D step 9 and the investigation record on issue #286. The acceptance gate does not read machine-local conversation records or any path under the gitignored `scratchPad/` tree, so it is reviewable and reproducible from this repository alone.

Automated acceptance runs against a **dedicated non-production credential and a staging or disposable endpoint**. It never uses a production credential and never issues requests against mutable production infrastructure.

Only after the disposable service passes:

1. Add the staging host through the local UI using a dedicated non-production credential issued for this test.
2. Trigger the next safe turn and confirm the successor sandbox request contains the host rule.
3. Run the same sandbox-auth fetch that previously returned `status: denied`.
4. The RED condition is the original policy denial recorded in the fixture: HTTP 403, `status: denied`, exit 10.
5. The GREEN condition is that the request passes the policy boundary and reaches the service. A 2xx proves full success; a 401/403 from the service proves policy refresh succeeded but the credential is invalid or insufficient and must be diagnosed separately.
6. Inspect all response payloads, browser traffic, persisted conversation records, and logs for the credential canary. No secret may appear.

**Optional manual confirmation against the incident host.** Confirming the original `kibana.faagun.com` incident is not part of the automated gate and is not required for the deployment decision, because steps 1-6 already prove the policy-refresh mechanism end to end. If an operator chooses to run it, it requires explicit written authorization from the owner of that system, a clearly non-production credential scoped to read-only access, and a manually recorded result. This design does not prescribe using the real production credential, and no automated job may do so.

## Phase F — full local regression gate

Run:

```powershell
dotnet build LmDotnetTools.sln -bl:.logs/build.binlog
dotnet test LmDotnetTools.sln --logger "trx;LogFileName=results.trx" --results-directory .logs/test-results
```

Read the TRX totals and structured JSONL test logs. The repository has no accepted pre-existing failures; every failure or flake must be explained and fixed before deployment.

# 7. Deployment Gate

Deployment is permitted only when:

- the policy-denied scenario has a local RED-to-GREEN proof against the staging or disposable endpoint, matching the committed fixture baseline;
- all secret-canary scans are clean;
- cross-origin, unauthenticated, non-admin, and missing-antiforgery browser tests all fail closed;
- the structural route tests pass: the anonymous allowlist is exactly the two bootstrap routes, every other management endpoint carries the policy, and no `api/auth/egress-keys` route remains mapped;
- the per-entry version tests pass, including the concurrent-rotation lost-update case;
- the policy revision is proven not to advance for credential-provider-only changes;
- the partition barrier tests prove no mid-turn replacement and no pinned partition;
- failed replacement preserves the predecessor session;
- host deletion removes the network rule after replacement, and no deletion leaves an unreconciled token tombstone;
- the single-instance lock, host, origin, and secret-presence startup checks all fail closed as specified;
- full build and test totals are green.

Production rollout should begin with one non-critical environment and monitor replacement failures, stale-revision counts, drain timeouts, authorization denials, unreconciled cleanup tombstones, and audit events. Credential values must never be included in telemetry.
