# Sandbox gateway auth enforcement — operator runbook

Issue #153 wires two independent auth boundaries around the sandbox gateway:

1. **Outbound (client → gateway):** every sample authenticates itself to the SandboxGateway with
   an `X-Sbx-App-Id` / `X-Sbx-App-Key` pair, enforced (or not) by the gateway's own
   `AUTH_ENFORCE` switch (M1).
2. **Inbound (caller → LmStreaming.Sample):** LmStreaming.Sample's headless REST API can require
   its own S2S callers (e.g. the Code-Review Daemon) to present a shared secret via `X-S2S-Auth`,
   and forwards the caller's own `X-Sbx-*` identity through to the gateway per-request (M2).

These are separate trust boundaries with separate secrets. Do not reuse one for the other.

## The four headers / keys at a glance

| Name | Direction | Purpose | Config key | Env var |
|---|---|---|---|---|
| `X-Sbx-App-Id` | Sample/daemon → SandboxGateway | Caller's app identity | `SandboxGateway:AppId` | (per-app; see below) |
| `X-Sbx-App-Key` | Sample/daemon → SandboxGateway | Caller's app secret (base64, ≥32 bytes) | `SandboxGateway:AppKey` | (per-app; see below) |
| `X-S2S-Auth` | S2S caller → LmStreaming.Sample REST API | Inbound shared secret gating headless endpoints | `Auth:S2SInboundSecret` | `LMSTREAMING_S2S_INBOUND_SECRET` |
| `CRD_SANDBOX_APP_ID` / `CRD_SANDBOX_APP_KEY` | Code-Review Daemon's own outbound identity | The daemon's `X-Sbx-*` pair when it talks to the gateway directly | n/a (env-only) | `CRD_SANDBOX_APP_ID` (default `codereview-daemon`), `CRD_SANDBOX_APP_KEY` |

`X-Sbx-App-Id` / `X-Sbx-App-Key` are read from `SandboxGateway:AppId` / `SandboxGateway:AppKey`
(`appsettings.json` or the standard ASP.NET Core env-var provider, e.g.
`SandboxGateway__AppKey`) in each process that talks to the gateway directly:
LmStreaming.Sample and the Code-Review Daemon sample each hold their **own** app identity —
`CRD_SANDBOX_APP_ID`/`CRD_SANDBOX_APP_KEY` is simply how the daemon's `Program.cs` populates
its own `SandboxGatewayOptions` at startup, not a separate protocol.

When LmStreaming.Sample's `ConversationsController` receives `X-Sbx-App-Id`/`X-Sbx-App-Key` on an
inbound headless request (from an S2S caller acting as a *different* app identity than the
sample's own default), it forwards that caller's credential to the gateway for that
conversation's lifetime instead of using its own default — see "Cross-actor resume" below.

## Deploy order for turning on gateway enforcement (`AUTH_ENFORCE`)

`AUTH_ENFORCE` is a SandboxGateway-side switch (not part of this repo) that decides whether the
gateway rejects requests missing/mismatching `X-Sbx-App-Id`/`X-Sbx-App-Key`. Flip it on in this
order to avoid an outage:

1. **Provision credentials.** Mint one app id + a base64 app key (≥32 bytes,
   `Convert.FromBase64String`-compatible — reject URL-safe base64) per caller identity that will
   talk to the gateway: at minimum one for LmStreaming.Sample, one for the Code-Review Daemon, and
   one per distinct S2S caller you want to track separately in the Cross-Actor Resume Matrix.
2. **Deploy the clients first, with `AUTH_ENFORCE` still off.** Set `SandboxGateway:AppId`/
   `SandboxGateway:AppKey` (and `CRD_SANDBOX_APP_ID`/`CRD_SANDBOX_APP_KEY` for the daemon) in every
   process. With enforcement off, the gateway ignores the headers, so this step is a no-op change
   in gateway behavior — it only proves the clients boot and send well-formed headers.
3. **Flip `AUTH_ENFORCE=on` on the gateway.** Only after every client in step 2 is confirmed
   sending headers. From this point, a request without a valid `X-Sbx-App-Id`/`X-Sbx-App-Key` pair
   is rejected by the gateway.
4. **(Optional, independent) Turn on the inbound `X-S2S-Auth` guard** by setting
   `Auth:S2SInboundSecret` (`LMSTREAMING_S2S_INBOUND_SECRET`) on LmStreaming.Sample once every S2S
   caller (e.g. the daemon) is updated to send the header. This is orthogonal to `AUTH_ENFORCE` —
   it gates LmStreaming.Sample's own REST API, not the gateway.

Rolling back is a plain client-build revert: the gateway's session cache is in-memory and
process-local (cleared on its own restart), so there is no persisted state to migrate either way.
In-flight interactive sessions simply recreate on the next message after a rollback.

### Compatibility matrix (gateway `AUTH_ENFORCE`)

| Client | Gateway `AUTH_ENFORCE` | Result |
|---|---|---|
| Old client (no `X-Sbx-*` headers) | `off` | Works (keyless dev path) |
| Old client (no `X-Sbx-*` headers) | `on` | **401** — expected; this is the failure mode this feature fixes |
| New client, headers sent | `off` | Works (gateway ignores the headers) |
| New client, headers sent, valid credential | `on` | Works |
| New client, headers sent, wrong/rotated-out key | `on` | 401/403 → surfaced as `sandbox_auth_failed` (distinct from `sandbox_unavailable` connectivity failures) |

### Compatibility matrix (LmStreaming.Sample `Auth:S2SInboundSecret`)

| Caller | `Auth:S2SInboundSecret` configured? | `X-S2S-Auth` header | Result |
|---|---|---|---|
| Any | Not configured (unset/blank) | (ignored) | Allowed — keyless dev path, one process-wide startup warning logged |
| Any | Configured | Missing | 401 |
| Any | Configured | Wrong value | 401 |
| Any | Configured | Matches (constant-time compare) | Allowed |

## Cross-actor resume (per-conversation identity binding)

A conversation is bound to the caller identity (`X-Sbx-App-Id`) that first created it, for its
lifetime:

| Creator | Continuer | Behavior |
|---|---|---|
| S2S caller A | S2S caller A | Continues under A |
| S2S caller A | S2S caller B | **409 Conflict** |
| S2S caller A | Plain UI (no credential) | **409 Conflict** |
| Plain UI (no credential) | S2S caller A | **409 Conflict** |
| Plain UI (no credential) | Plain UI (no credential) | Continues under the sample's own default identity |

`ConversationsController.SendMessage` maps the pool's `SandboxCredentialConflictException` to a
`409` with a body naming only the conflicting app ids (never app keys):
`{ "error": "caller_credential_conflict", "code": "caller_credential_conflict", "detail": "...", "threadId": "..." }`.
This binding is in-memory only; a process restart clears it, but the gateway's own per-app session
scoping is the durable backstop (a foreign `AppId` addressing a known session id still 404s at the
gateway).

## Credential rotation

**Outbound `X-Sbx-App-Id`/`X-Sbx-App-Key` (per app):** these are plain env-var/config values held
statically for the process lifetime — there is no live reload. Rotation is:

1. Provision a new app key for the same app id (or a new app id, if rotating identity too) at the
   gateway.
2. Update `SandboxGateway:AppKey` (or `CRD_SANDBOX_APP_KEY`) in the target process's
   configuration/environment.
3. Restart the process to pick up the new value.
4. Deprovision the old key at the gateway once every process using it has restarted.

There is a brief window during step 2–3 where the old key is still configured; this is the same
single-key rotation window as any static-secret rollout in this codebase (see `AuthSharedSecret`
usage elsewhere) and is acceptable because the gateway keeps the old key valid until step 4.

**Inbound `Auth:S2SInboundSecret`:** the current implementation validates against exactly one
configured secret value (`Auth:S2SInboundSecret`, no primary/secondary pair), so rotation is also
restart-based: provision the new secret value, update every S2S caller to send it, update the
config value, then restart LmStreaming.Sample. There is a short window where callers using the old
secret get 401s across the restart. A primary+secondary secret (accept either during a rotation
window, matching the zero-downtime approach `AuthSharedSecret` supports elsewhere) was considered
in the design (`decisions.md` BS3) but is not implemented in this slice — treat it as a follow-up if
the restart-based window is unacceptable for a given deployment.

## Never-log invariants

Neither `X-Sbx-App-Key` nor `X-S2S-Auth`'s configured/presented value is ever written to logs or
included in any response body, in any of the components documented here. Errors surfaced to
callers (401 bodies, `SandboxCredentialConflictException` messages, `sandbox_auth_failed`) name
app **ids** only, never app **keys** or the S2S secret. If you add a new log statement anywhere on
these paths, keep this invariant.
