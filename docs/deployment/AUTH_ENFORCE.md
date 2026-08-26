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
during design but is not implemented in this slice — treat it as a follow-up if the restart-based
window is unacceptable for a given deployment.

> This paragraph used to cite a `decisions.md` item "BS3" for that design consideration. No such
> file exists, and none ever did: it was never added, renamed or deleted in this repository's
> history on any branch. It was a local planning scratchpad kept during the #153 M2 session
> (2026-07-04 – 07-08) and never committed, so it is not recoverable. The citation is dropped
> rather than left dangling — see #315. Don't go looking for it.

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

Match it against the token's **`preferred_username`** claim and nothing else (spec §8.2, #349). A
token can carry `email` and `upn` as well, and those are not the same guarantee: `email` in
particular is a directory attribute a user may be able to set, so accepting it would let someone
claim the pending admin row by typing the operator's address into their own profile. If the value
you have came from an email header or a business card rather than from the directory's
`preferred_username`, confirm it before onboarding.

> **The first admin cannot bind if the app registration issues v1.0 access tokens.** A v1.0 access
> token emits `upn`/`unique_name` and **no** `preferred_username`, and binding reads only
> `preferred_username`. The admin's sign-in then succeeds as an ordinary member, the admin row is
> never claimed, and the tenant has no administrator. This is not silent: `PrincipalFactory` logs a
> **warning** naming the tenant and the missing claim on each such sign-in — grep the host log for
> `First-admin binding skipped` if a freshly onboarded tenant has no working admin. **Recovery:**
> configure the SPA's Entra app registration to request **v2.0** tokens (`accessTokenAcceptedVersion:
> 2` on the API app's manifest, which is what makes Entra emit `preferred_username`), then have the
> intended admin sign in again. There is no operator override that binds an admin by object id after
> the fact; re-provisioning the same directory is refused with `entra_tenant_id_claimed`, so fixing
> the token version is the path.

`entraTenantId` is normalised before it is stored and before it is matched, so the directory GUID
may be pasted in any form the portal or a script produces — upper or lower case, braced, wrapped in
parentheses, or unhyphenated (#347). One directory therefore cannot end up as two rows because two
operators pasted it differently.

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

### What `Identity:Enforce` refuses, and what it filters

Read this before turning it on anywhere real.

With `Identity:Enforce` true:

- Every `/api` request must carry a valid token from a **provisioned** tenant (slice 1).
- Every conversation **REST** route resolves the caller against the conversation's owner columns
  before answering (slice 2, #302) — both `ConversationsController` and the workspace file browser at
  `/api/conversations/{threadId}/files`, which addresses the same conversations by the same ids.
  Reads, writes, deletes and shares of another tenant's conversation are refused, and the
  conversation listing returns only what the caller may see. The WebSocket transports are the
  exception and are listed under "Known gaps" below.

A refusal that would otherwise confirm a conversation id exists answers `404` with the same body an
id that was never minted produces. That is deliberate: conversation ids are guessable enough that a
`403` would be an existence oracle. A caller who already holds a grant gets `403` with a reason code
instead, because for them there is nothing left to hide.

**The refusals also cost the same shape of work (#389).** Identical bodies are not enough on their
own: three different refusals reach that same `404` — the id names no row, the row belongs to
another tenant, and the row is in the caller's own tenant but grants them nothing — and only the
last of them used to consult the grant registry. One extra round trip is a small signal, but it is a
signal, and it is one an authenticated member could read as "this id exists inside my tenant". The
authorizer now issues that same lookup on the paths that would otherwise skip it, so all three
answers make exactly one grant-registry call. For a caller with no end user (an `Identity:Apps`
service credential) the equalised count is zero, because such a principal never consults grants on
any path.

Scope this claim precisely, in both directions:

- It equalises the **shape** of the work, not the wall clock. Nothing here defends against an
  attacker who can measure the cost of the *store* itself — a cached row against a cold one, say.
  Wall-clock equalisation is not attempted, and a runbook should not claim it.
- What was leaking before the fix was **existence only, and only inside the caller's own tenant** —
  never contents, never whether a grant exists, never anything about a tenant the caller is not a
  member of. Cross-tenant probing was already clean.
- Closing the oracle **amplifies grant-store load** under a probe: a stream of guessed ids that used
  to cost zero grant-registry lookups (the id named no row) now costs one lookup each, since the
  missing-row and wrong-tenant paths take the same lookup the found-but-forbidden path always did.
  That is the intended trade — a fixed one call per authenticated request, not per row — but size the
  grant store for it, and keep the standard per-caller request rate limit in front of these routes so
  the equalised lookup cannot be turned into an unbounded amplifier.

Rights follow spec §7.4.1, and two of them surprise people:

- A **tenant admin** may READ a member's private conversation but may not write or delete it.
- A **grantee** may not re-share, whatever their role. Sharing stays with the owner, so revoking a
  grant actually revokes access.

With `Identity:Enforce` false nothing above applies and every route behaves exactly as it did before
slice 2. The owner columns are still written, so a deployment accumulates correct ownership data
while enforcement is off — which is what makes the flip reversible.

### Every conversation must carry a tenant before you flip

Under enforcement a conversation whose `tenant_id` is null is visible to **nobody** — not its
author, not an admin. Conversations written by builds predating #302 have no tenant, so they must be
claimed first. Two mechanisms do this, and both are needed:

1. **The startup repair.** On every boot the host ensures the quarantine tenant named by
   `Identity:LegacyTenantId` (default `legacy`) exists, then stamps every conversation that has no
   tenant with it. It runs on every boot rather than once, because a host rolled back to a build
   predating #302 writes untenanted conversations again and a version-gated migration would never
   revisit them.

   If `Identity:LegacyTenantId` names a tenant that already exists and is not the quarantine tenant,
   the host **refuses to start** and writes nothing. Stamping real customer data with a real
   customer's tenant id would hand that customer's admins read access to it. Pick an unused id.

   Agent-owned threads — the `subagent-*` and `workflow-*` ids the agent pool creates rather than
   the provisioning route — used to reach this repair as fresh untenanted rows, claimed only at the
   NEXT boot. They now **inherit** tenant and owner from their parent conversation at creation
   (#385), so the repair no longer has to catch up with them.

   Inheritance rather than re-resolution, deliberately: a sub-agent run outlives the HTTP request
   that started it, and the request principal is gone by the time the child thread is created. The
   parent's stored row is the only identity still available, and it is the correct one — the child
   is work done on the parent's behalf.

   Inheritance is only as good as the parent's tenant at the instant the child is created, so the
   startup repair is still load-bearing, not vestigial: a child minted while its parent is itself
   still untenanted — the ordinary state with `Identity:Enforce` off, or a brief stamping-order
   window where the child's row lands before the parent's stamp — inherits nothing and stays
   untenanted until the next boot's repair claims it. Because such a child can therefore outlive its
   parent's stamping while still un-stamped itself, the persisted sub-agent roster scan admits its
   root's tenant **or** an untenanted row, so a still-untenanted descendant is never dropped from the
   roster it belongs to.

   **Visibility is not inherited.** Tenant and owner are identity; publication is a decision someone
   made about the parent conversation, and it was not a decision about this one. A child of a
   tenant-published parent starts private, and publishing it stays an explicit act.

2. **Adoption.** `POST /api/admin/tenants/{tenantId}/adopt-legacy` moves conversations out of the
   quarantine tenant into a real one, optionally assigning an owner. It takes the operator secret,
   like every other route on that controller.

   ```jsonc
   {
     "resourceType": "thread",
     "ownerUserId": "{entra-tid}:{oid}",  // optional; omit to adopt without an owner
     "resourceIds": ["thread-1", "thread-2"],  // optional; omit for every quarantined conversation
     "dryRun": true
   }
   ```

   Notes that matter:
   - `dryRun: true` reports the count and a sample of up to 20 ids and writes no customer data. It
     still writes an audit record.
   - An **omitted** `resourceIds` means "every quarantined conversation". An **empty array** means
     "none". They are not the same, and the route does not conflate them.
   - `ownerUserId` must belong to the target tenant's Entra directory or the call is refused with
     `owner_tenant_mismatch` before anything is written.
   - Re-running adopts nothing the first run already moved: the selection is on the SOURCE tenant,
     so a retry after a timeout is safe.
   - A conversation still in the quarantine tenant after the flip is invisible to end users. That is
     the intended failure mode — inaccessible, not exposed — but it is an outage for whoever owned
     it, so rehearse first.

### Service callers under enforcement (`Identity:Apps`)

A service caller has no user to sign in, so it gets its own front door (spec §4.2 step 1): the
inbound S2S secret proves it is a known service, and an `Identity:Apps` entry says which tenant that
service acts within. The result is a principal with `Source = AppOnly` and no `OnBehalfOf` — the app
acting as itself, never as a user.

```jsonc
{
  "Identity": {
    "Enforce": true,
    "Apps": {
      // Keyed by the caller's X-Sbx-App-Id. The daemon's own id is "codereview-daemon".
      "codereview-daemon": { "TenantId": "tnt_acme", "Scopes": [] },

      // Used when a caller presents X-S2S-Auth with no X-Sbx-App-Id at all.
      "default": { "TenantId": "tnt_acme" }
    }
  }
}
```

Onboarding is explicit, exactly like tenants, and for the same reason: the alternative is that
anyone holding the shared secret picks their own tenant id.

| Request | `Identity:Enforce` | Result |
|---|---|---|
| Correct `X-S2S-Auth`, app id present in `Identity:Apps` | `true` | Principal minted; the endpoint's own `InboundS2SAuthAttribute` still runs and still checks the secret |
| Correct `X-S2S-Auth`, app id **not** in `Identity:Apps` | `true` | `403` `service_app_not_registered` — the caller authenticated, so retrying cannot help |
| Correct `X-S2S-Auth`, registration names `Identity:LegacyTenantId` | `true` | `403` `service_app_tenant_invalid` — no principal may carry the quarantine tenant (spec §8.5.2) |
| Wrong or missing `X-S2S-Auth` | `true` | `401` |
| `X-S2S-Auth` presented while `Auth:S2SInboundSecret` is unset | `true` | `401`, and an error is logged. The keyless dev path disables the endpoint guard; minting a principal there would let anyone who typed two header names in |
| Anything | `false` | Unchanged — the development principal, exactly as before |

The registration does not replace `Auth:S2SInboundSecret`; it reads the same value and calls the
same constant-time comparison the endpoint filter uses, so the two can never disagree about what a
service request is.

**Infrastructure callbacks sit outside this boundary entirely.** `/api/auth/webhook/*` and
`/api/lifecycle/*` have no user and no tenant to resolve, and each carries its own credential — the
gateway's deferred-auth webhook puts a *session secret* in `Authorization`, which the JWT handler
cannot parse by design, and the lifecycle control plane runs its own signature check and is off by
default. Guarding them would refuse every legitimate caller and grant nothing. That exemption is
asserted, not trusted: a test enumerates this host's real endpoint table and requires every `/api`
route to be either guarded or named on `IdentityMiddleware.UnguardedApiPaths`, so a newly added
route cannot land outside the boundary silently.

`/api/auth/egress-keys*` is **not** one of these, despite looking like one. Its controller presents
no credential — it is loopback-gated only — and the SPA reaches it through `apiFetch`, which attaches
the bearer token under enforcement exactly as it does for `/api/workspaces`. It therefore stays
*inside* the boundary, guarded like any other management route; carving it out would have let a
credential-less loopback caller plant, read and destroy egress keys under enforcement (that was the
state briefly introduced by an earlier draft of #345 and corrected before the flip).

### Cross-origin clients and refusals (#346)

CORS is registered **before** the identity middleware. Both halves of that matter:

- A CORS preflight is an `OPTIONS` request with no `Authorization` header — browsers never attach
  one, by specification. An identity middleware in front of CORS answers `401`, and the browser then
  abandons the real request without ever sending it. (The middleware also lets a genuine preflight
  through on its own, as a second, independent guard; a bare `OPTIONS` carrying no
  `Access-Control-Request-Method` is still guarded.)
- A refusal written downstream of the CORS middleware leaves **without**
  `Access-Control-Allow-Origin`, so a cross-origin SPA sees an opaque network error instead of the
  stable code in `X-Identity-Refusal` that the refusal exists to communicate.

Set `LmStreaming:AllowedOrigins` to the origins that may call this host. It is empty by default,
which is correct for the bundled same-origin SPA: the CORS middleware is still registered
(`LmStreaming:EnableCors` defaults to `true`), but with no origins allowed it answers every
cross-origin request without an `Access-Control-Allow-Origin` header, so no other site can read a
response. CORS is skipped entirely only when `LmStreaming:EnableCors` is set to `false`.

### Recommended flip order

1. Deploy the build. Leave `Identity:Enforce` false. The schema migrates (`user_version` 4), the
   startup repair stamps untenanted conversations with the quarantine tenant, and new conversations
   are stamped with their creator's tenant and user as they are created. Nothing is refused yet.
2. Register the Entra app, set `AzureAd:ClientId`. Sign-in now works and is exercised.
3. Set `LMSTREAMING_IDENTITY_OPERATOR_SECRET` and provision every tenant that will need one. Confirm
   each expected user signs in and resolves to the tenant you expect.
4. Adopt the legacy data. Run `adopt-legacy` with `dryRun: true` for each target tenant, check the
   counts against what you expect, then run it for real.
5. Confirm nothing is left behind: a `dryRun` adoption into any real tenant should now report `0`,
   and no conversation anyone still needs should remain in the quarantine tenant.
6. **Onboard every service caller** that talks to this host, by adding an `Identity:Apps` entry
   naming the tenant it acts within (see "Service callers" below). A caller with no entry gets `403`
   under enforcement. Do this before step 7, not after — an S2S caller cannot sign in to fix itself.
7. **If a browser on another origin calls this host**, set `LmStreaming:AllowedOrigins` to that
   origin. Refusals are answered before the endpoint runs, so a cross-origin client can only read
   the refusal code if this host is configured to allow its origin.
8. Set `Identity:Enforce` true. Anonymous `/api` requests now get `401`.

Steps 1 through 7 are reversible and can sit in production for as long as you like. Only step 8
changes what any caller sees.

### Known gaps

One thing `Identity:Enforce` still does not do that its name suggests it might, plus three that used
to be listed here and are now fixed. The list is exhaustive over the REST surface: every other `/api`
route that names a conversation goes through the authorizer.

#### Service callers used to be refused (#345, fixed)

This was the largest gap and is now closed; the section is kept because operators reading an older
runbook will look for it. Before the fix, `Identity:Enforce=true` answered `401` to every caller
under `/api` that did not present an Entra bearer token, including callers that authenticated
correctly by another mechanism. `InboundS2SAuthAttribute` is an `IAsyncActionFilter`, so it ran at
endpoint execution — long after the middleware had already written the `401`. A correct S2S request
never reached its own guard. See "Service callers" below for how they authenticate now.

#### An editor grantee used to collide with a live agent (#376, fixed)

The agent pool binds a conversation's live agent to the user who started it and refuses a second
user's turn on the same thread with `409 principal_conflict`. That guard predates named sharing, so
an **editor** grantee whose write the policy had already allowed was refused by the cache rather
than by a decision — intermittently, because the conflict cleared whenever the agent was evicted.

The three routes that mutate through the pool — send, mode switch, provider switch — now **release**
an agent bound to a different user, after the authorization allows and before they touch the pool.
The pool's guard itself is unchanged: a caller with no grant never reaches the release and is still
refused as unknown, so the release cannot be turned into a way to evict a stranger's agent by id.

**The sandbox answer, which is the part operators need:** a grantee does **not** inherit the owner's
sandbox. Sharing a conversation grants the conversation — whose history is durable and rehydrates
onto the new agent — not the filesystem the owner's agent was provisioned. Two users writing through
one agent would share whatever that sandbox holds, and revoking the grant would not take it back. So
a handoff costs one sandbox provision plus the pooled agent's in-memory-only state, paid only when a
conversation actually changes hands. A conversation two people write to alternately pays it on each
change of hands; if that becomes a real workload, the fix is per-caller agent entries, not a wider
guard.

A run **in progress** is left alone: the release does not happen, and the second caller still gets
`409 principal_conflict`. Evicting mid-run would abort the streaming turn of whoever is mid-answer —
the wrong party to punish for someone else's handoff — and would be a race any second caller could
trigger at will. Retry once the run ends.

The app-id freeze (`caller_credential_conflict`) is deliberately **not** released alongside it. That
is the boundary between services rather than between people: an app-only S2S caller has no
`EffectiveUserId` to hold a grant with, so there is no authorization verdict that could stand in for
one, and the cross-actor resume matrix (#153) pins that refusal on purpose.

#### The WebSocket transports used to carry no token (#342, fixed)

`/ws` and `/ws/subagent` sat outside the `/api` prefix the identity middleware guarded, and the
browser WebSocket API admits no custom headers — so under `Identity:Enforce=true` every REST route
demanded a principal while the transport that actually carries the conversation demanded nothing.

The handshake now carries the credential in the **`Sec-WebSocket-Protocol`** header, as an offered
subprotocol `lm.bearer.<token>` alongside the application subprotocol `lm.chat.v1`. It is a header,
so the token never reaches a URL, a proxy log, a `Referer`, or browser history — which is what rules
out the query-string scheme this gap used to propose. Before authentication runs, the middleware
promotes that token into `Authorization: Bearer <token>` and strips it from the offered list, so the
socket converges on the **same front doors as REST** (the JWT bearer handler and the
`IRequestPrincipalSource` chain) and the two cannot drift apart. An `Authorization` header already on
the request is never overwritten: a caller able to set headers has already presented its credential
the stronger way.

An unauthenticated handshake is refused with **`403`, not `401`** — `websocket_authentication_required`.
A `401` tells a browser to re-authenticate, and a browser that re-authenticates and reconnects into
the same refusal loops. REST keeps its `401`; only the WebSocket transports answer `403`.

**Operational consequence when you flip `Identity:Enforce` on:** a cached older SPA build offers no
subprotocol and its socket is refused. Ship the client change with the flip, or expect chat to fail
to connect for anyone holding a stale bundle. With enforcement **off** nothing changes: no token is
offered, the handshake is admitted exactly as before, and the development principal is used.

#### The WebSocket transports do no per-conversation authorization

Still open, and narrower than the gap above it. `/ws` and `/ws/subagent` now establish **who** the
caller is, and the pooled agent they create is owned by that user (#399) — so a second user cannot
resume someone else's live agent over the socket. What they do **not** do is ask
`ConversationAuthorizer` whether this user may open *this* conversation: the REST routes' grant and
tenant checks have no equivalent on the socket.

The reason it is not simply added is that the client mints a `threadId` and opens the socket before
any metadata row exists, so an authorizer call at handshake time would refuse every brand-new
conversation as unknown. Closing it needs the socket to distinguish "not yours" from "not yet
minted". Until then: a caller who knows another user's `threadId` can open a socket on it. Every
REST route over that same conversation still refuses them, and the agent the socket reaches is bound
to whoever created it.
