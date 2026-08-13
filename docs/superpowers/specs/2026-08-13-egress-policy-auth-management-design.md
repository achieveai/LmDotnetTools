# Egress Policy Refresh and Remote-Safe Egress Auth Management

**Status:** Approved design
**Date:** 2026-08-13

## Problem

Predefined Egress Auth entries serve two separate purposes:

1. They contribute destination hosts and credential-provider references to a sandbox's `network.rules` and `auth_providers` during sandbox creation.
2. They provide current credential values to the authenticated gateway webhook when a request needs authorization.

The first part is immutable for the life of a sandbox. Adding, changing, or deleting a host does not update an existing sandbox. The second part is resolved at request time, so replacing credentials for an already-authorized host can take effect without rebuilding network policy.

The management API is currently designed for local use and relies on a network-location check rather than authenticated administrator identity. That boundary is not sufficient for browser-based or remotely exposed administration. Detailed reproduction mechanics are intentionally omitted until the authorization and browser-isolation controls described here are implemented.

The management UI must work from any legitimate URL where the application is exposed. Existing and submitted credentials must never be returned to the browser.

## Goals

1. Apply host-policy additions, changes, and deletions to active workspaces without requiring a manual conversation restart.
2. Avoid interrupting an active run or creating a no-session window.
3. Allow Egress Auth management from any deployment hostname through authenticated, policy-authorized, same-origin requests.
4. Preserve a strict write-only secret boundary.
5. Prevent a metadata edit from silently moving an existing credential to a different host.
6. Provide local verification at every layer before deployment.

## Non-goals

- Add a gateway API for mutating a running sandbox's network policy.
- Return masked, partial, encrypted, or recoverable credential values to the browser.
- Change the gateway auth webhook's per-session-secret boundary.
- Implement a general-purpose secrets manager.
- Make wildcard cross-origin administration possible.

# 1. Policy and Credential Lifecycle

## 1.1 Policy generation

`PredefinedKeyRegistry` owns a monotonically increasing in-process policy generation.

The generation advances when an operation changes the sandbox policy shape:

- create an entry;
- change an entry's host;
- change its credential kind or provider identity;
- delete an entry.

Credential-only rotation for an unchanged host/provider does not advance the generation. The gateway webhook already resolves current credential values per request, subject to its bounded cache TTL.

Each cached sandbox session records the generation used to create it.

## 1.2 Safe replacement

A dedicated policy-freshness operation runs from each dispatch/provisioning path before beginning new sandbox-backed work. It must not make every generic `GetOrCreateLiveSessionAsync` call a replacement boundary, because some callers acquire a session while a larger review or agent run is already active.

The interactive WebSocket path, REST/S2S message path, and daemon run-provisioning path call this operation before starting a new run. It waits for work already bound to the affected `(workspaceId, callerAppId)` partition to become idle, then compares the cached session generation with the registry generation.

When stale:

1. Keep the predecessor session available.
2. Create a successor from the current registry snapshot.
3. Verify successor creation succeeded.
4. Atomically replace the `(workspaceId, callerAppId)` cache binding only if it still points to the expected predecessor.
5. Record predecessor/successor linkage through the existing replacement metadata.
6. Retire the predecessor after the swap.
7. If creation or compare-and-swap fails, delete the unused successor and keep the predecessor binding.

The implementation must not copy the current 404 recovery ordering, which evicts before creating and therefore permits a no-session window.

## 1.3 Active-run rule

Policy replacement occurs before dispatching a new turn, not in the middle of an active turn. Existing active runs finish against their established sandbox. The next turn observes the new policy.

A host deletion also advances the generation. The webhook will stop returning credentials immediately, but the old sandbox retains its network allow rule until replacement; replacement closes that residual network reachability.

# 2. Remote Management Security

## 2.1 Authentication and authorization

The sample adds a small server-managed admin session rather than exposing a reusable management secret to browser JavaScript.

Configuration supplies one installation-level `EgressAuthAdmin` secret from an environment variable or external secret store. It is never present in checked-in configuration or returned through an endpoint. An operator enters it into a same-origin unlock form. The server compares it in constant time and, on success, issues a short-lived, Secure, HttpOnly, SameSite=Strict admin cookie. The browser does not persist the entered secret and clears the input after submission.

Apply an `EgressAuthAdmin` authorization policy structurally to the entire controller or route group. The policy accepts only the authenticated admin-session principal. Every current and future management action inherits it.

Requirements:

- The unlock endpoint is rate-limited and emits only success/failure audit metadata.
- The admin cookie has bounded idle and absolute lifetimes and supports explicit logout/revocation.
- Remote management fails closed when the installation secret is absent.
- Non-loopback access requires HTTPS.
- Forwarded headers are accepted only from configured known proxies or networks.
- The existing loopback check may remain as an optional development restriction, but it is not authorization.

The gateway-facing `AuthWebhookController` remains separate. It continues authenticating with the per-session secret and may return credential headers only to the gateway after destination validation.

## 2.2 CORS and CSRF

Apply `[DisableCors]` or an equivalent no-cross-origin endpoint policy to the unlock and Egress Auth controllers so they do not inherit wildcard CORS. The bundled client uses same-origin relative URLs, so it works from any legitimate deployment hostname.

Every state-changing request, including unlock, logout, POST/PUT/PATCH, and DELETE, requires ASP.NET Core antiforgery validation. Origin validation is defense in depth, not a substitute for authentication or antiforgery.

`AllowedHosts` and trusted forwarded-header configuration must describe the deployment rather than trusting arbitrary `Host` or `X-Forwarded-*` values.

This admin-session mechanism is intentionally scoped to Egress Auth. It is not a general user-account or role-management system.

## 2.3 Local bootstrap

For local testing, the admin secret is supplied through a process environment variable. The UI is opened from the sample's own origin, the operator unlocks the session, and all subsequent metadata and mutation calls use the HttpOnly cookie plus antiforgery token. No credential is placed in browser local storage, session storage, a URL, or client-side configuration.

# 3. Secret-Safe API Contract

## 3.1 Read model

The read DTO contains metadata only:

- entry ID;
- display name, host, and kind;
- configured header names;
- booleans such as `hasClientSecret` and `hasRefreshToken`;
- timestamps and policy-refresh state.

It has no fields capable of carrying header values, API keys, client secrets, refresh tokens, or reusable masked placeholders. Keep a reflection-based test that rejects secret-bearing properties on the response type.

## 3.2 Metadata changes

`PATCH /api/auth/egress-keys/{id}` edits non-secret metadata. It never accepts secret fields.

A host, credential kind, provider identity, custom-header-name set, token endpoint, or client ID change is routing/authentication-sensitive. The metadata endpoint rejects such a change with `409 credential_replacement_required`; the browser must use the replacement operation below. Display-only metadata may be edited without touching the stored secret.

## 3.3 Secret and routing replacement

`PUT /api/auth/egress-keys/{id}/configuration` atomically replaces all routing-sensitive metadata and all required credentials. It never merges an existing credential into a new host/provider configuration. Creating an entry uses the same complete write-only request shape.

A credential-only rotation for an unchanged policy shape may use `PUT /api/auth/egress-keys/{id}/credential`. Both operations return `204 No Content` or metadata only. The submitted value is never placed in:

- response DTOs;
- structured log properties;
- exception messages;
- audit records;
- model-state serialization;
- browser-readable configuration.

Blank or omitted secret fields mean "leave unchanged." They never mean clear and are never represented by a mask token.

## 3.4 Clear and delete

OAuth entries may expose an explicit clear-optional-refresh-token operation where the resulting entry remains valid. Required credentials cannot be cleared while retaining an unusable entry; delete the entry instead.

Custom-header entries have no clear-all operation because every configured header requires a non-empty value. Individual removal is performed through the complete configuration-replacement operation, which supplies the full remaining header-name/value set. Deleting an entry is explicit and advances the policy generation.

Clear and delete actions have confirmation in the UI.

## 3.5 Policy refresh states

Metadata responses expose one of these states:

- `current`: all cached partitions use the current policy generation;
- `pending`: at least one partition is stale but is waiting for a safe boundary;
- `refreshing`: successor creation or swap is in progress;
- `failed`: the predecessor remains bound after a refresh failure; a later boundary may retry.

The state contains no session secret, auth token, or credential material.

## 3.6 Audit events

Audit actor, action, entry ID, host, kind, timestamp, policy generation, and affected session count. Never log or destructure a secret-bearing request object.

# 4. Client Behavior

The modal lists metadata and configured/not-configured indicators only.

Editing a secret starts with an empty input. Leaving it empty preserves the existing value. Replacing it sends a new value once. The browser never receives the current value.

Host changes require an explicit new credential in the same workflow. The client cannot submit a masked sentinel as if it were a credential.

After a policy-shape change, the UI reports that active workspaces will refresh at their next safe turn. It does not claim immediate propagation before the replacement succeeds.

# 5. Error Handling

- Unauthorized and forbidden responses reveal no entry metadata.
- Validation errors identify fields and constraints but never include submitted secret values.
- A failed successor creation leaves the predecessor bound and returns or records a retryable refresh state.
- A failed successor cleanup is best-effort and logged without secrets, matching the current lifecycle cleanup contract; adding a durable cleanup retry queue is out of scope.
- A compare-and-swap loss deletes the unused candidate and uses the winning binding.
- Credential rotation failures do not advance the policy generation unless policy shape changed.

# 6. Local-First Verification Plan

No deployment begins until this ladder passes locally.

## Phase A — unit tests

### Management response and update contract

1. Keep the reflection assertion that the response DTO exposes no secret-bearing fields.
2. Serialize every response shape and assert that known submitted secret canaries are absent.
3. Verify credential replacement returns `204` or metadata only.
4. Verify blank/omitted input preserves an existing secret.
5. Verify explicit clear removes it.
6. Verify a new blank credential is rejected.
7. Verify a host change cannot silently preserve the old credential.
8. Verify error messages and captured structured logs never contain secret canaries.

### Authorization

1. Every controller action rejects an unauthenticated caller.
2. An authenticated caller without `EgressAuthAdmin` is forbidden.
3. An authorized caller succeeds from both loopback and a simulated non-loopback address.
4. Add a structural test proving every management endpoint carries the policy; a future action cannot omit it accidentally.
5. Verify remote mode fails closed when authentication configuration is absent.

### Policy generation

1. Create, host change, kind/provider change, and delete increment generation.
2. Credential-only rotation does not increment generation.
3. Failed persistence does not publish an incremented generation.
4. Concurrent updates produce a monotonic, observable generation.

## Phase B — registry/session integration tests

Use a fake gateway that records sandbox-create and delete requests.

1. Create generation N session and assert its request contains the expected host rule.
2. Rotate only the credential and assert no replacement create occurs.
3. Add a host, begin the next turn, and assert a successor is created with generation N+1 and both expected rules.
4. Assert the predecessor remains bound until the successor create succeeds.
5. Force successor creation failure and assert the predecessor remains usable.
6. Force compare-and-swap loss and assert the unused successor is deleted.
7. Delete a host and assert the successor no longer contains its network rule.
8. Prove no replacement happens in the middle of an active run; it occurs at the next dispatch boundary.
9. Assert each `(workspaceId, callerAppId)` partition refreshes independently and retains credential isolation.
10. Verify replacement metadata identifies predecessor and successor correctly.

## Phase C — browser security tests

Run the sample locally and use Playwright through the repository's existing task-isolated testing workflow.

1. Open the legitimate UI from its actual origin and verify list/create/edit/delete work for an authorized user.
2. Run a repository-local browser-security regression fixture from an untrusted origin against every management route. Keep the fixture's transport details private until the authorization fix ships.
3. Assert every cross-origin read and mutation fails closed.
4. For cookie authentication, omit or corrupt the antiforgery token and assert every mutation fails.
5. Verify a valid antiforgery token succeeds.
6. Inspect browser network responses and assert no response contains the submitted secret canary.
7. Confirm the edit form starts empty and never renders an existing secret or masked reusable value.
8. Repeat behind a local reverse proxy using configured trusted forwarded headers; verify an untrusted spoofed forwarded header does not grant access or alter scheme/client identity.

## Phase D — real local sandbox test

Use the locally published LmStreaming.Sample, gateway, and egress proxy.

1. Start with a sandbox whose policy does not contain a local test HTTPS service host.
2. Invoke the installed sandbox-auth skill helper at `/marketplaces/claude-plugins/sandbox-auth/scripts/sandbox-auth-fetch.py` and prove the request is denied by policy. This helper is supplied by the mounted marketplace, not authored by this repository; the test preflight must assert that the path exists and otherwise report the environment as not runnable.
3. Add the service through the legitimate UI without restarting the application or conversation.
4. Trigger the next turn and observe one successor sandbox creation.
5. Repeat the fetch and prove it reaches the service with the expected injected test credential.
6. Rotate the credential only and prove no sandbox replacement occurs; after the gateway cache TTL, the service receives the new credential.
7. Delete the entry, trigger the next turn, and prove the replacement policy no longer allows the host.
8. Verify application, gateway, proxy, browser, and structured test logs contain none of the secret canaries.

Use a disposable local HTTPS echo service for Phases A-D. It must report only whether the expected credential matched, not print the credential value.

## Phase E — Kibana-targeted local acceptance

The reproducible incident baseline is recorded in `scratchpad/conversation_memories/kibana-connectivity-investigation/diagnosis.md`, including the persisted conversation evidence for HTTP 403, `status: denied`, and exit 10.

Only after the disposable service passes:

1. Add `kibana.faagun.com` through the local UI using the real server-side credential.
2. Trigger the next safe turn and confirm the successor sandbox request contains the Kibana host rule.
3. Run the same sandbox-auth fetch that previously returned `status: denied`.
4. The RED condition is the original policy denial: HTTP 403, `status: denied`, exit 10.
5. The GREEN condition is that the request passes the policy boundary and reaches Kibana. A Kibana 2xx proves full success; a Kibana 401/403 proves policy refresh succeeded but the credential is invalid or insufficient and must be diagnosed separately.
6. Inspect all response payloads, browser traffic, persisted conversation records, and logs for the credential canary. No secret may appear.

## Phase F — full local regression gate

Run:

```powershell
dotnet build LmDotnetTools.sln -bl:.logs/build.binlog
dotnet test LmDotnetTools.sln --logger "trx;LogFileName=results.trx" --results-directory .logs/test-results
```

Read the TRX totals and structured JSONL test logs. The repository has no accepted pre-existing failures; every failure or flake must be explained and fixed before deployment.

# 7. Deployment Gate

Deployment is permitted only when:

- the original policy-denied scenario has a local RED-to-GREEN proof;
- all secret-canary scans are clean;
- cross-origin and unauthorized browser tests fail closed;
- failed replacement preserves the predecessor session;
- host deletion removes the network rule after replacement;
- full build and test totals are green.

Production rollout should begin with one non-critical environment and monitor replacement failures, stale-generation counts, authorization denials, and audit events. Credential values must never be included in telemetry.
