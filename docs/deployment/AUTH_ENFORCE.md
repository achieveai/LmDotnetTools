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
| `X-S2S-Auth` | S2S caller → LmStreaming.Sample REST API | Inbound shared secret gating headless endpoints | `Auth:S2SInboundSecret` | `LMSTREAMING_S2S_INBOUND_SECRET` (bridged to the config key at startup) |
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
   `LMSTREAMING_S2S_INBOUND_SECRET` (bridged into `Auth:S2SInboundSecret` at startup; the section key
   also accepts `Auth__S2SInboundSecret` or an `appsettings.json` value directly) on LmStreaming.Sample
   once every S2S caller (e.g. the daemon) is updated to send the header. This is orthogonal to
   `AUTH_ENFORCE` — it gates LmStreaming.Sample's own credential-passthrough REST surface, not the
   gateway, and only for requests that carry an S2S marker (see the compatibility matrix below); the
   same-origin SPA is unaffected.

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

The inbound guard is **marker-gated**: it enforces the shared secret only on *service-to-service*
requests — those carrying an `X-S2S-Auth` header or an `X-Sbx-App-Id` caller-credential marker (the
header that asks the sample to forward a distinct identity to the gateway). A same-origin browser
request from the bundled SPA carries neither marker, so it is always allowed through and runs under
the sample's own gateway identity. This is deliberate: turning the secret on locks down the
credential-passthrough / headless surface **without** breaking the interactive UI, which calls the
same `/api/conversations*` routes with plain `fetch` and correctly holds no S2S secret. It is **not**
a blanket lock on the same-origin interactive API — an operator who needs every route (including
same-origin) authenticated should front the app with a real browser-auth mechanism (cookie/OIDC),
not this S2S service secret.

| Caller / request | `Auth:S2SInboundSecret` configured? | S2S marker present? | `X-S2S-Auth` header | Result |
|---|---|---|---|---|
| Any | Not configured (unset/blank) | (either) | (ignored) | Allowed — keyless dev path, one process-wide startup warning logged |
| Same-origin SPA (browser `fetch`) | Configured | No (`X-S2S-Auth` and `X-Sbx-App-Id` both absent) | Absent | Allowed — runs under the sample's own identity |
| S2S caller | Configured | Yes | Missing | 401 |
| S2S caller | Configured | Yes | Wrong value | 401 |
| S2S caller | Configured | Yes | Matches (constant-time compare) | Allowed |

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

## A third boundary: end-user sign-in (`Identity:Enforce`, issue #301)

The two boundaries above authenticate *services*. Neither of them establishes **who the human
is** — a browser request carries no `X-Sbx-*` pair and no `X-S2S-Auth`, and passes both guards
untouched. P1 adds a third, independent boundary for that, with its own switch and its own secret.
As above: do not reuse one boundary's secret for another.

| Name | Direction | Purpose | Config key | Env var |
|---|---|---|---|---|
| `Authorization: Bearer` | Browser → LmStreaming.Sample | The signed-in user's Entra access token | `AzureAd:ClientId` / `AzureAd:TenantId` | n/a (public SPA client) |
| `X-Operator-Secret` | Operator → `POST /api/admin/tenants` | Authorises creating a tenant | `Identity:OperatorSecret` | `LMSTREAMING_IDENTITY_OPERATOR_SECRET` (bridged to the config key at startup) |

### `Identity:Enforce` is global

One process-wide flag, not per tenant. With it **false** (the default) an unauthenticated `/api`
request resolves to a development principal instead of being refused, so every existing call path
and every existing test keeps working. With it **true**, anonymous `/api` requests get `401` — for
every customer in that process at once. A shared deployment cannot stage the flip customer by
customer; that is the trade made in exchange for one unambiguous answer to "is this deployment
enforcing?".

Leaving `AzureAd:ClientId` empty is a second, independent off switch: with no client id, no JWT
bearer handler is registered at all and no token can be presented.

### Tenants are provisioned explicitly, before anyone signs in

A first sign-in from an unknown Entra directory is a **rejection**, never an implicit new tenant.
The user gets `403` with a stable code — `tenant_not_provisioned`, or `tenant_suspended` for a
directory that is known but stopped — and the SPA renders a screen explaining it. That response
deliberately carries no redirect and no `WWW-Authenticate` challenge: a challenge would send the
browser back to Entra, and signing in again cannot conjure a provisioned tenant, so it would loop.

Onboard a customer before their first sign-in:

```bash
curl -X POST http://<host>/api/admin/tenants \
  -H "X-Operator-Secret: $LMSTREAMING_IDENTITY_OPERATOR_SECRET" \
  -H "Content-Type: application/json" \
  -d '{"tenantId":"tnt_acme","entraTenantId":"<their entra tid guid>",
       "displayName":"Acme Corp","firstAdminUpn":"dana@acme.example"}'
```

`firstAdminUpn` is recorded now and bound to that person's durable `{tid}:{oid}` id on their first
successful sign-in — the operator cannot know their object id yet. It binds **once**; a later
sign-in by anyone whose UPN was reassigned to that mailbox does not take the admin row over.

Two properties of this route differ from `X-S2S-Auth` on purpose, and both matter:

- **It is unconditional.** `InboundS2SAuthAttribute` only enforces on requests carrying an S2S
  marker header. This one always requires its header.
- **It fails closed.** `InboundS2SAuthAttribute` disables itself when its secret is blank, which is
  right for a same-origin UI path that would otherwise break. Here an unset secret answers `503`
  and never succeeds — the alternative failure mode is a world-writable tenant registry.

`Identity:SeedTenants` provisions tenants from configuration at startup, idempotently, and is
**ignored entirely while `Identity:Enforce` is true**. It exists for development and single-tenant
installs. Configuration files get copied between environments, and a stale entry that could mint a
real tenant in an enforcing deployment would defeat explicit provisioning through its own
convenience feature.

### `Identity:Enforce` gives you authentication, NOT yet authorization

Read this before turning it on anywhere real.

With `Identity:Enforce` true, every `/api` request must carry a valid token from a **provisioned**
tenant. That is the whole of what slice 1 delivers. It does **not** filter any data by tenant.
`IResourceAccessPolicy` is a contract with no implementation yet, no controller consults it, and
`ConversationsController` does not read the `Principal` at all.

So a signed-in user from tenant A can still reach conversations and workspaces belonging to
tenant B, exactly as before this change. What `Enforce` buys today is that anonymous callers are
turned away and every request is attributable in the audit trail — a front door, not a partition.

Per-tenant and per-owner access checks land in slice 2 (#302). Do not read "identity is enforced"
as "tenant data is isolated" until that ships.

### Recommended flip order

1. Register the Entra app, set `AzureAd:ClientId`; leave `Identity:Enforce` false. Sign-in now
   works and is exercised, but nothing is refused.
2. Set `LMSTREAMING_IDENTITY_OPERATOR_SECRET` and provision every tenant that will need one.
   Confirm each expected user signs in and resolves to the tenant you expect.
3. Set `Identity:Enforce` true. Anonymous `/api` requests now get `401` — **and so does every
   service-to-service caller**, including ones that authenticate correctly with `X-S2S-Auth`. Read
   "Service callers are refused" below before taking this step. Do not take it at all on a host
   that serves S2S traffic.

### Known gaps

Two things `Identity:Enforce` does not do that its name suggests it might. Both are open.

#### Service callers are refused (#345)

`Identity:Enforce=true` answers `401` to every caller under `/api` that does not present an Entra
bearer token — including callers that authenticate correctly by another mechanism.

`IdentityMiddleware` guards the whole `/api` prefix and builds a principal from exactly one source:
the resolution the JWT bearer handler stashes while validating an Entra token. `InboundS2SAuthAttribute`
is an `IAsyncActionFilter`, so it runs at endpoint execution — long after the middleware has already
written the `401`. A correct S2S request never reaches its own guard.

Concretely, with `Enforce` true these all get `401`:

| Caller | Route | How it authenticates |
|---|---|---|
| Any S2S caller | `/api/conversations*` and every other guarded route | `X-S2S-Auth` (+ `X-Sbx-App-Id`) |
| Sandbox gateway deferred-auth callback | `/api/auth/webhook/{provider}` | A session secret in `Authorization` — which the JWT handler will also fail to parse |
| Egress key issuance | `/api/auth/egress-keys` | Its own guard |
| Lifecycle approvals / subscriptions | `/api/lifecycle/*` | Its own guard |

The `/api/identity/config`, `/api/admin/tenants` and `/api/health` prefixes are exempt and stay
reachable; nothing else is.

**The Code-Review Daemon's review host is a live instance of this.** The daemon stamps `X-S2S-Auth`
and `X-Sbx-App-Id` on every call to that host's `/api/conversations` routes and holds no Entra token.
Flipping `Identity:Enforce` true there stops the daemon reviewing.

This is fail-closed, not a bypass — no unauthenticated caller gets in. But it is a total outage for
non-browser callers, so **do not flip `Identity:Enforce` on a host serving S2S traffic** until #345
lands. The fix is a principal path for service callers (spec §4.2, slice 5 / #305), not an exemption
list in the middleware.

#### The WebSocket transports carry no token

The two WebSocket transports (`/ws` and `/ws/subagent`) carry no token: the browser WebSocket API
admits no custom headers, and `/ws` sits outside the `/api` prefix the identity middleware guards.
They remain unauthenticated even with `Identity:Enforce` true. Closing that needs a query-string or
first-frame scheme and is tracked on issue #301.
