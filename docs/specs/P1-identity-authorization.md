# P1 - Identity, Tenancy and Authorization

Status: Draft
Epic: [#293](https://github.com/achieveai/LmDotnetTools/issues/293) - P1 - Identity, Tenancy & Managed Cloud
Extends the design recorded in [#237](https://github.com/achieveai/LmDotnetTools/issues/237).

This spec defines how a request acquires an identity, how that identity is carried through the
codebase, and how data is scoped to it. It is written so that a single delivery slice
(section 11) can be implemented without reading the rest of the pillar.

---

## 1. Scope and non-goals

### 1.1 What this spec covers

The platform sells governed autonomous agents and must be **multi-user, multi-tenant, and
multi-app** across three deployment modes:

| Mode | Shape | Who asserts the human |
|---|---|---|
| **A** | Embedded chat component (iframe) inside a customer page | The host backend, via a short-lived **embed token** |
| **B** | Agents running inside a customer's app; the app exposes functions the LLM calls, with fail-closed approval (`docs/adrs/0003-fail-closed-tool-approval.md`) | The host service, via a signed **RFC 8693 on-behalf-of JWT** presented behind its app credential |
| **C** | Fully headless daemons | The daemon's own app credential; a human principal only where one initiated the work |

In scope:

1. A single `Principal` abstraction that every request resolves to, regardless of front door.
2. Interactive sign-in with **Microsoft Entra ID** for the SPA.
3. Host-asserted end users on the service-to-service path (modes B and C).
4. Host-minted **embed tokens** for mode A.
5. Private-by-default resources with **named-user sharing** - conversations, workspaces, chat modes.
6. Concrete schema changes and a migration plan for rows that already exist.
7. Attribution of the existing usage ledger to a principal and a tenant.
8. An evaluation of an identity-broker layer (Zitadel-style) against going direct to Entra.

### 1.2 Non-goals

- **Building an IdP.** No password storage, no MFA implementation, no account recovery. Entra owns
  credentials.
- **SCIM / directory sync.** Users are provisioned lazily on first successful sign-in.
- **Group and team management.** Roles are flat within a tenant (`member`, `admin`) until a
  concrete requirement forces otherwise.
- **ReBAC / policy engine.** Named-user grants on three resource types do not justify one.
- **Link sharing.** Sharing is to named users only. An unguessable URL is not an authorization
  decision.
- **Changing gateway `app.id` ownership semantics.** The `X-Sbx-App-Id` binding is an authenticated
  tenancy boundary owned by the gateway repository (its ADR 0029, in
  `SandboxedOstoolsMcpServer/Docs/adrs/`). This spec adds a user dimension *beside* it and never
  replaces it.
- **Per-tenant data residency, BYOK, or physical isolation.** Row-level tenant scoping only.

### 1.3 Vocabulary

This repository already has auth vocabulary. Reuse it; do not invent synonyms.

| Concept | Existing term | Defined in |
|---|---|---|
| Calling service identity | **app id** / `SandboxCredential.AppId`, header `X-Sbx-App-Id` | `src/LmAgentInfra/Sandbox/SandboxCredential.cs:12,18` |
| Calling service secret | **app key** / `AppKey`, header `X-Sbx-App-Key` | `src/LmAgentInfra/Sandbox/SandboxCredential.cs:21` |
| Identity that *confers ownership* | **caller credential** (vs **effective credential**, which always has a value and falls back to the process default) | `docs/adrs/0005-service-to-service-lifecycle-delivery.md`; `src/LmAgentInfra/Sandbox/SandboxSessionRegistry.cs:97-106` |
| Inbound service-to-service secret | header `X-S2S-Auth`, config `Auth:S2SInboundSecret` | `samples/LmStreaming.Sample/Controllers/ConversationsController.cs:68,71` |
| Enforcement gated on a request marker | **marker-gated** | `ConversationsController.cs:120-126` |
| Behaviour when config is absent | **fail closed** | `docs/adrs/0003-fail-closed-tool-approval.md` |
| Secret never returned or logged | **never-log invariant**, **write-only** | `docs/deployment/AUTH_ENFORCE.md` |
| Ownership conflict on resume | `caller_credential_conflict` (HTTP 409) | `ConversationsController.cs:759` |

New terms this spec introduces: **principal**, **tenant**, **grant**, **embed token**,
**on-behalf-of (OBO) JWT**. Nothing else.

Note two traps for readers:

- `NonOwningConversationStore` (`samples/LmStreaming.Sample/Persistence/NonOwningConversationStore.cs`)
  is about *disposal* ownership, not authorization. Do not extend it for principals.
- The egress-auth design's `EgressAuthAdmin` scheme
  (`docs/superpowers/specs/2026-08-13-egress-policy-auth-management-design.md`) is an
  **operator** console session for managing destination-host credentials. It is a different
  trust boundary from end-user sign-in and must not be merged with it.

---

## 2. Verified current state

Every statement below was checked against the tree at `d757d4be`.

### 2.1 There is no end-user identity anywhere

Repository-wide, `src/` and `samples/` contain **zero** occurrences of `AddAuthentication`,
`UseAuthentication`, `UseAuthorization`, or `[Authorize]`. No `Microsoft.Identity.Web` and no
`Microsoft.AspNetCore.Authentication.JwtBearer` package reference exists. The ASP.NET Core
authentication stack is entirely unused.

The request pipeline in `samples/LmStreaming.Sample/Program.cs` is, in order:

```
2056  app.UseSerilogRequestLogging(...)
2068  app.UseViteDevelopmentServer(true)     // Development only
2072  app.UseStaticFiles()
2075  app.UseLmStreaming()                   // UseWebSockets + UseCors
2078  app.Map(...)
2207  app.Map(...)
2270  app.MapControllers()
2276  app.MapGet("/", -> /dist/index.html)
2281  app.MapFallbackToFile("dist/index.html")
```

There is no authentication step. Authentication middleware must be inserted between
`UseStaticFiles()` (line 2072) and `UseLmStreaming()` (line 2075).

### 2.2 The only identity is the app id, and the UI has none

`SandboxCredential` is a `readonly record struct` of exactly two fields, `AppId` and `AppKey`
(`src/LmAgentInfra/Sandbox/SandboxCredential.cs:12`). It identifies a *calling service*.

`MultiTurnAgentPool` freezes a conversation to the app id that created it. The pool dictionary is
keyed by `threadId` alone (`src/LmAgentInfra/Agents/MultiTurnAgentPool.cs:49`); the freeze is an
inline comparison inside `GetOrCreateAgent`, repeated in two more places
(`MultiTurnAgentPool.cs:462-467`, `:1080-1085`, `:1371`):

```csharp
var existingAppId = existing.CallerCredential?.AppId;
var requestedAppId = callerCredential?.AppId;
if (!string.Equals(existingAppId, requestedAppId, StringComparison.Ordinal))
    throw new SandboxCredentialConflictException(threadId, existingAppId, requestedAppId);
```

The comment above it states the consequence plainly: "both null (two plain UI callers) matches".
**Every interactive browser user has a `null` caller credential, so every human matches every
other human.** In the UI, this guard does nothing. That is the hole P1 closes.

### 2.3 Inbound service-to-service auth is one shared secret, marker-gated

`InboundS2SAuthAttribute` (`samples/LmStreaming.Sample/Controllers/ConversationsController.cs:60-161`)
is an `IAsyncActionFilter` with two independent gates:

1. If `Auth:S2SInboundSecret` (bridged from `LMSTREAMING_S2S_INBOUND_SECRET`) is blank, the guard
   is **disabled entirely** and logs one process-wide warning (`:88-93`, `:128-141`).
2. Otherwise it enforces only on requests carrying an S2S marker - the `X-S2S-Auth` header or the
   `X-Sbx-App-Id` header (`IsServiceToServiceRequest`, `:120-126`). Same-origin SPA requests carry
   neither and pass through unauthenticated (`:95-102`).

Failure is `401` with `{ "error": "unauthorized", "code": "s2s_auth_failed" }` (`:107`), compared
in constant time over SHA-256 digests (`:150-160`).

It is applied to `ConversationsController` (`:165`), `WorkspacesController`
(`samples/LmStreaming.Sample/Controllers/WorkspacesController.cs:11`), and `FileBrowserController`.
**It is not applied to `ChatModesController`** - that controller has no inbound guard at all.

### 2.4 Listing endpoints are unscoped

`GET /api/conversations` (`ConversationsController.cs:302-336`) calls
`store.ListThreadsAsync(limit, offset, ct)` and applies exactly one filter -
`SubAgentSummary.IsAgentOwnedThreadId`, which hides sub-agent and workflow threads from the
sidebar. It is unrelated to caller identity.

`GET /api/workspaces` lives in a **separate controller**,
`samples/LmStreaming.Sample/Controllers/WorkspacesController.cs:18-50`, and calls
`IWorkspaceStore.GetAllAsync(ct)` - an interface method that takes no filter parameter at all
(`samples/LmStreaming.Sample/Persistence/IWorkspaceStore.cs:14`).

Every one of the fourteen endpoints on `ConversationsController` is addressed purely by
`threadId`. Knowing an id is sufficient to read, mutate, or delete a conversation.

### 2.5 Storage has no owner and no migration mechanism

`src/LmMultiTurn/Persistence/Sqlite/SqliteSchemaInitializer.cs` creates eight tables. The relevant
one is:

```sql
CREATE TABLE IF NOT EXISTS thread_metadata (
    thread_id      TEXT PRIMARY KEY,
    current_run_id TEXT,
    last_updated   INTEGER NOT NULL,
    metadata_json  TEXT
);
```

No owner, user, tenant, or app-id column exists on any of the eight tables.

**There is no migration mechanism.** Every statement is `CREATE TABLE IF NOT EXISTS`, run in one
transaction on every open (`SqliteSchemaInitializer.cs:175-210`). There is no schema-version table
and no `PRAGMA user_version` use. The comment at `:150-151` states this is deliberate: new *tables*
appear on next open. Adding a *column* to an existing table has no path today. Slice #301 must
build one, because the `tenants` table (8.2) lands in that slice.

`ThreadMetadata` (`src/LmMultiTurn/Persistence/ThreadMetadata.cs`) carries an extensible
`Properties` bag. **Ownership must not go in the property bag**: it is serialized into
`metadata_json` and cannot be filtered, indexed, or joined in SQL, and `ListThreadsAsync` must
filter in the database, not in memory after the fact.

### 2.6 Workspaces and chat modes are flat JSON files, not tables

- `Workspace` (`samples/LmStreaming.Sample/Models/Workspace.cs:10-60`) - fields `Id`, `Name`,
  `DirectoryRelPath`, `Marketplaces`, `PluginSelection`, `PluginsRevision`, `IsSystemDefined`,
  `CreatedAt`, `UpdatedAt`. Persisted by `FileWorkspaceStore` into a single `workspaces.json`.
- `ChatMode` (`samples/LmStreaming.Sample/Models/ChatMode.cs:6`) - fields `Id`, `Name`,
  `Description`, `SystemPrompt`, `EnabledTools`, `EnabledBuiltInTools`, `IsSystemDefined`,
  `CreatedAt`, `UpdatedAt`. Persisted by `FileChatModeStore` into a single `chat-modes.json`,
  registered as a process-wide singleton at `Program.cs:597`. System modes come from an in-memory
  `SystemChatModes.All`.

Neither has an owner or tenant field. `IsSystemDefined` distinguishes read-only built-ins from
user-created entries; it does not say *which* user. The chosen mode id is stored per conversation
under `MultiTurnAgentPool.ModePropertyKey = "mode"` (`MultiTurnAgentPool.cs:45`) - per conversation,
never per user.

Because these are JSON documents rather than tables, slices #303 and #304 add **record fields**,
not columns, and the migration is a document rewrite rather than DDL.

### 2.7 The usage ledger measures correctly but attributes to a conversation

`UsageRecord` (`src/LmCore/Models/UsageRecord.cs:61-131`) carries `LogicalCallId`,
`ProviderAttemptId`, `Revision`, `RootConversationId`, `ParentExecutionId`, `ExecutionKind`,
`RequestedModel`, `EffectiveModel`, the five token counters, `EstimatedPublicCostMicros`,
`ProviderReportedCostMicros`, `Currency`, and `Finalized`. There is **no principal, user, or tenant
field**.

`UsageLedger` (`src/LmMultiTurn/UsageAccounting/UsageLedger.cs:11`) dedupes by `ProviderAttemptId`
and is scoped to one `RootConversationId`.

Persistence is the important correction: **there is no usage table.**
`ConversationUsageProjection.SaveAsync`
(`src/LmMultiTurn/UsageAccounting/ConversationUsageProjection.cs:52-117`) writes through
`IConversationStore.UpdateMetadataAsync` into `ThreadMetadata.Properties` under the keys
`usage.aggregate` (`:22`) and `usage.records` (`:25`) - which land in the same
`thread_metadata.metadata_json` blob. A tenant-admin view that aggregates spend across users cannot
be served by scanning JSON blobs; slice #306 needs a real table.

### 2.8 Entra plumbing already exists - for tool delegation, not sign-in

`src/LmAgentInfra/Auth/` contains a full OAuth stack: `OAuthProviderBase`, `PkceHelper`,
`OAuthTokenEndpointClient`, `IOAuthTokenStore`/`FileOAuthTokenStore`, `PendingAuthCoordinator`,
`DeferredInteractiveAuthPolicy`, plus `M365OAuthProvider`, `AdoOAuthProvider`, and
`GitHubOAuthProvider`. `Microsoft.Identity.Client` (MSAL) 4.84.1 is referenced by both
`src/LmAgentInfra` and `samples/LmStreaming.Sample`, and the providers already construct Entra v2.0
authorities:

```csharp
var authority = $"https://login.microsoftonline.com/{_options.TenantId}";   // M365OAuthProvider.cs:73
$"https://login.microsoftonline.com/{tenantId}/oauth2/v2.0/authorize";      // M365OAuthProvider.cs:324
```

`AdoAuthController`, `M365AuthController`, `GitHubAuthController`, and `AuthPagesController` already
handle the redirect legs. This is **egress**: acquiring tokens so a tool can call ADO, Graph, or
GitHub *on the operator's behalf*. It is not inbound sign-in and confers no identity on the request.

The practical consequence is favourable: the repo already knows Entra authorities, PKCE, token
stores, and MSAL. Slice #301 adds inbound validation, not a new dependency family.

### 2.9 CORS is wide open

`UseLmStreaming()` calls `UseCors` when `LmStreamingOptions.EnableCors` is true, and the defaults
are `EnableCors = true` with `AllowedOrigins = ["*"]`
(`src/LmStreaming.AspNetCore/Configuration/LmStreamingOptions.cs:21,26`), producing
`AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()`. The sample does not override them
(`Program.cs:133-137`). There is no antiforgery configuration anywhere.

`AllowAnyOrigin` is incompatible with credentialed requests, which is one reason section 4 chooses
bearer tokens over cookies. Tightening `AllowedOrigins` to a configured allow-list is part of slice
#301.

### 2.10 Project layout constrains where `Principal` can live

```
LmAgentInfra (net9.0) -> LmMultiTurn (net8.0;net9.0) -> LmCore (net8.0;net9.0, no project refs)
samples/LmStreaming.Sample (net9.0)
```

`LmAgentInfra` is net9.0-only and holds `SandboxCredential`, but `LmMultiTurn` - which owns
`IConversationStore` and must filter by owner - cannot reference it. `LmCore` has no project
references, multi-targets `net8.0;net9.0`, and already hosts `UsageRecord`.

**`Principal` therefore belongs in `LmCore`.** It must compile under net8.0 and must not depend on
ASP.NET Core types.

---

## 3. The `Principal` model

### 3.1 Shape

New file: `src/LmCore/Identity/Principal.cs`. Pure value types, no ASP.NET dependency, net8.0-safe.

```csharp
namespace AchieveAi.LmDotnetTools.LmCore.Identity;

/// <summary>What kind of party a <see cref="PrincipalRef"/> names.</summary>
public enum PrincipalKind
{
    /// <summary>A calling service authenticated by its app credential.</summary>
    App = 0,
    /// <summary>A human authenticated by an identity provider.</summary>
    EndUser = 1,
    /// <summary>An autonomous agent run. Always has an OnBehalfOf.</summary>
    Agent = 2,
    /// <summary>A platform-internal service with no external caller.</summary>
    Service = 3,
}

/// <summary>One named party. Immutable, comparable, safe to log in full.</summary>
public readonly record struct PrincipalRef(PrincipalKind Kind, string Id);

/// <summary>
/// The authenticated identity of one request or one agent run. Constructed once at an
/// authentication boundary and never mutated.
/// </summary>
public sealed record Principal
{
    /// <summary>Organisation this request operates within. Never null once authenticated.</summary>
    public required string TenantId { get; init; }

    /// <summary>The party that actually made the call.</summary>
    public required PrincipalRef Actor { get; init; }

    /// <summary>
    /// The party the actor is acting for, when the actor is not acting for itself. Null for a
    /// human signing in directly.
    /// </summary>
    public PrincipalRef? OnBehalfOf { get; init; }

    /// <summary>
    /// Prior actors, outermost-first, from a nested RFC 8693 <c>act</c> chain. Audit only -
    /// never consulted for an access decision.
    /// </summary>
    public IReadOnlyList<PrincipalRef> DelegationChain { get; init; } = [];

    /// <summary>App id from the app credential, when one authenticated the call.</summary>
    public string? AppId { get; init; }

    /// <summary>Granted scopes, already intersected (see 3.2). Ordinal comparison.</summary>
    public IReadOnlySet<string> Scopes { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>Tenant-level roles, e.g. <c>member</c>, <c>admin</c>.</summary>
    public IReadOnlySet<string> Roles { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>Which front door authenticated this request. Audit and diagnostics only.</summary>
    public required PrincipalSource Source { get; init; }

    /// <summary>
    /// The user this activity is attributed to: <see cref="OnBehalfOf"/> when it names an
    /// EndUser, else <see cref="Actor"/> when it is an EndUser, else null. This is the value
    /// written to owner columns and usage records.
    /// </summary>
    public string? EffectiveUserId =>
        OnBehalfOf is { Kind: PrincipalKind.EndUser } obo ? obo.Id
        : Actor is { Kind: PrincipalKind.EndUser } a ? a.Id
        : null;
}

public enum PrincipalSource
{
    /// <summary>Interactive Entra sign-in from the SPA.</summary>
    Interactive = 0,
    /// <summary>App credential plus host-asserted on-behalf-of JWT.</summary>
    HostAsserted = 1,
    /// <summary>Host-minted embed token (mode A).</summary>
    Embed = 2,
    /// <summary>App credential alone, no human asserted.</summary>
    AppOnly = 3,
    /// <summary>Process-internal, e.g. a background daemon acting as itself.</summary>
    Internal = 4,
}
```

### 3.2 The composition rule (normative)

**Effective permissions are the INTERSECTION of the actor's and the on-behalf-of party's
permissions - never the union.**

An app acting for a user can never exceed that user. A user acting through an app can never exceed
the app. A union would be a confused-deputy hole: a host application with broad scopes could
silently widen what any of its low-privilege users can do.

This rule is inherited unchanged from #237 and is the single most important invariant in this spec.
`Principal.Scopes` is populated already-intersected, so no downstream caller can get it wrong by
forgetting to intersect.

`DelegationChain` records history for audit only. Consistent with RFC 8693, which states that a
consumer "MUST only consider the token's top-level claims and the party identified as the current
actor by the `act` claim", only `Actor` and `OnBehalfOf` participate in a decision.

### 3.3 User identity keys

An end-user id is the namespaced pair `{tid}:{oid}` taken from the Entra access token, where `tid`
is the tenant GUID and `oid` is the immutable object id of the user.

- **Not `sub`.** Entra's `sub` is a pairwise identifier scoped to the (user, client app) pair, so
  the same human gets a different `sub` in a different app registration.
- **Not `preferred_username` or `email`.** Both are mutable and re-assignable.
- **Not `oid` alone.** `oid` is unique only within a tenant; a guest present in two tenants has two
  `oid` values. Namespacing with `tid` makes the key globally unique and makes cross-tenant
  collision structurally impossible.

`Principal.TenantId` is **our internal tenant id** (`tnt_*`), not the Entra `tid`. It is resolved by
looking the token's `tid` up in the `tenants` table (8.2). An unresolved `tid` is a rejected
sign-in, never an implicit new tenant (4.4). The user id keeps the Entra `tid` in its prefix
because it must stay globally unique independently of our own records - the two ids do different
jobs and both are needed.

### 3.4 How `Principal` flows - recommendation

**Recommendation: construct once at the edge into a DI-scoped accessor, then pass it explicitly as
a parameter across every non-web boundary. Do not use an ambient `AsyncLocal`.**

Three layers:

1. **Web layer** (`samples/LmStreaming.Sample`, net9.0). A scoped
   `IPrincipalAccessor { Principal? Current { get; } }` backed by `IHttpContextAccessor`, populated
   by the authentication middleware. Controllers read it once per action.
2. **Boundary calls.** `Principal` is passed as an ordinary method parameter into
   `IConversationStore`, `IWorkspaceStore`, `IChatModeStore`, and `MultiTurnAgentPool` - exactly
   mirroring how `SandboxCredential? callerCredential` is passed today
   (`ConversationsController.cs:682`, `MultiTurnAgentPool.cs:1478`).
3. **Long-lived agent state.** `MultiTurnAgentPool.AgentEntry` gains a `Principal? OwnerPrincipal`
   captured at creation, beside the existing `CallerCredential` (`MultiTurnAgentPool.cs:148`).

**Why not ambient.** This is decided by existing code, not preference. `AgentEntry.RunTask` is a
background task that **outlives the HTTP request that started it** - that is the whole point of
`POST /api/conversations/{threadId}/messages` returning while the agent keeps running and
streaming. An `AsyncLocal` or `HttpContext`-derived ambient read from inside the agent loop would
be null, or worse, would be a *different* user's context if a pooled thread was reused. Capturing
the principal onto the entry at creation is the only correct option, and it is the pattern
`CallerCredential` already establishes.

**Why not `ClaimsPrincipal` everywhere.** `LmCore` and `LmMultiTurn` multi-target `net8.0;net9.0`
and have no ASP.NET dependency. `System.Security.Claims` is available, but a raw claims bag pushes
claim-name parsing into every consumer and makes the intersection rule (3.2) unenforceable. The
authentication handler translates `ClaimsPrincipal` to `Principal` exactly once, at the edge.

**Why not extend `SandboxCredential`.** It is a `readonly record struct` with two positional
members, consumed by precompiled downstream code and by the gateway SDK in `src/Sandbox`. Adding
members changes a published positional record. `Principal` is additive and separate; the two travel
together.

### 3.5 Where a `Principal` is constructed

Exactly four places, and nowhere else:

| Source | Constructed in | Front door |
|---|---|---|
| `Interactive` | Entra JWT bearer handler `OnTokenValidated` | 4.1 |
| `HostAsserted` | `InboundS2SAuthAttribute` successor, after OBO JWT validation | 4.2 |
| `Embed` | Embed-token validation filter | 6 |
| `AppOnly` / `Internal` | App credential alone, or process bootstrap | 4.2 |

A helper `PrincipalFactory` in the sample owns all four paths so the intersection rule is applied
in one place.

---

## 4. The two front doors

Both doors terminate in a `Principal` and nothing downstream can tell them apart.

```
                    +-----------------------------------+
  Browser SPA ----> | Entra JWT bearer                  |
   (mode A host,    |   validate iss/aud/sig/tid        |--\
    mode A itself)  +-----------------------------------+   \
                                                             \    +---------------+
  Host service ---> +-----------------------------------+     +-> |   Principal   | -> stores,
   (modes B, C)     | 1. X-Sbx-App-Id / X-Sbx-App-Key   |     /   |   (LmCore)    |    pool,
                    | 2. X-S2S-Auth  (existing)         |----/    +---------------+    ledger
                    | 3. Authorization: Bearer <OBO>    |    /
                    +-----------------------------------+   /
                                                            /
  Embedded iframe -> +----------------------------------+  /
   (mode A)          | Embed token (host-minted)        |-/
                     +----------------------------------+
```

### 4.1 Interactive sign-in - Entra, SPA

**Flow.** OIDC authorization code with PKCE, run in the browser (MSAL Browser), acquiring an access
token for this API's app registration. The API validates the bearer token; it does not issue a
session cookie.

**Why bearer, not cookie.** Three reasons, all grounded in current code: CORS is `AllowAnyOrigin`
today (2.9) and `AllowAnyOrigin` cannot carry credentials; there is no antiforgery configuration
and a cookie session would immediately require CSRF defence; and mode A embeds the SPA cross-site
in an iframe, where a cookie needs `SameSite=None; Secure` plus `Partitioned` (CHIPS) to survive
third-party-cookie deprecation. A bearer token held in memory sidesteps all three.

**Server configuration.** Add `Microsoft.Identity.Web` and register:

```csharp
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddMicrosoftIdentityWebApi(builder.Configuration.GetSection("AzureAd"));
```

```json
{
  "AzureAd": {
    "Instance": "https://login.microsoftonline.com/",
    "ClientId": "<api app registration client id>",
    "TenantId": "organizations"
  },
  "Identity": {
    "Enforce": false
  }
}
```

`Microsoft.Identity.Web` is chosen over raw `JwtBearer` because it implements multi-tenant issuer
validation correctly out of the box (5.1 step 3) and because MSAL (`Microsoft.Identity.Client`
4.84.1) is already a dependency of both `src/LmAgentInfra` and the sample.

**`TenantId: organizations`** admits any work or school tenant but not personal Microsoft accounts.
Issuer validation alone only proves *some* real Entra tenant signed the token - it does not prove
the tenant is a customer. The `tid` claim is therefore resolved against the `tenants` table (8.2) as
a separate authorization step, and an unprovisioned `tid` is rejected with `403` and an audit
record. There is no `AllowedTenants` config list: the tenant registry is data, not configuration,
because tenants are provisioned per customer at runtime (4.4).

**Pipeline placement.** `UseAuthentication()` then `UseAuthorization()` between `Program.cs:2072`
and `:2075`. `UseCors` inside `UseLmStreaming()` already precedes `MapControllers`; the
`AllowedOrigins` default must change from `["*"]` to a configured list in the same slice.

**Rollout, and the enforcement flag.**

> **Decision: `Identity:Enforce` is a single, process-wide flag - global, not per tenant.**
> This supersedes the per-tenant `IDENTITY_ENFORCE` described in
> [#237](https://github.com/achieveai/LmDotnetTools/issues/237). Enforcement is a property of the
> deployment, not of a customer.

`Identity:Enforce=true` requires a validated token on every `/api/*` route.

`Identity:Enforce=false` (the default) keeps every current call path working. Concretely, an
unauthenticated interactive request is not rejected and does not produce a null principal; it
resolves to the **development principal**:

```
Principal {
    Source     = Interactive,
    Actor      = (EndUser, "dev:local"),
    OnBehalfOf = null,
    TenantId   = Identity:LegacyTenantId (default "legacy"),
    AppId      = null,
    Roles      = ["admin"],
}
```

Two properties make this safe to reason about. It is a **real** `Principal`, so no code path needs a
null check and no test needs a second code path - which is what keeps the existing suite green. And
it never authorizes anything by its own contents: with `Enforce=false`, `IResourceAccessPolicy`
short-circuits at step 0 of 7.4 to `Allow("enforcement_disabled")` before looking at tenant, owner,
or role. The `admin` role and the quarantine tenant id are there so that listing queries and UI
affordances behave sensibly in development, **not** because the policy consults them.

The corollary matters for reviewers: `Enforce=false` is not "authorization with a permissive
principal", it is "authorization off". Nothing about the development principal should be read as a
security boundary, and the flip to `true` is what turns the model on.

This mirrors the `AUTH_ENFORCE` deploy discipline in `docs/deployment/AUTH_ENFORCE.md`: deploy with
enforcement off, onboard callers, then flip.

The consequence to be aware of is that a shared deployment cannot stage the flip customer by
customer - when it flips, it flips for everyone in that process. Staging across customers is done
by deploying them to separate instances, not by a per-tenant flag. That is the trade accepted in
exchange for one unambiguous global answer to "is this deployment enforcing?", which a per-tenant
flag cannot give: with per-tenant flags, no single check tells an operator whether the system as a
whole is safe.

### 4.2 Service-to-service - app credential plus host-asserted OBO JWT

Modes B and C. Three layers on one request, each answering a different question:

| Layer | Header | Answers |
|---|---|---|
| App credential | `X-Sbx-App-Id`, `X-Sbx-App-Key` | Which service is calling? |
| Inbound S2S secret | `X-S2S-Auth` | Is it allowed to call this API at all? |
| OBO JWT | `Authorization: Bearer <jwt>` | Which human is it acting for? |

**The OBO JWT is presented *behind* the app credential, never instead of it.** The app credential
remains the authenticated tenancy boundary; the OBO JWT is additive and can only *narrow*. A
principal lacking a scope is refused even when the app credential has it - this is the intersection
rule (3.2) applied at the front door.

**Where it lands.** `InboundS2SAuthAttribute`
(`samples/LmStreaming.Sample/Controllers/ConversationsController.cs:60-161`) is the existing seam
and is extended rather than replaced. Its current marker-gate stays exactly as it is: a request
carrying neither `X-S2S-Auth` nor `X-Sbx-App-Id` is still the interactive path and is handled by
4.1. After the existing constant-time secret check succeeds, the filter:

1. Reads the `Authorization: Bearer` header. Absent -> `Principal` with `Source = AppOnly`,
   `Actor = (App, appId)`, `OnBehalfOf = null`. Existing callers keep working unchanged.
2. Present -> validate per section 5. On success, build `Principal` with `Actor = (App, appId)`,
   `OnBehalfOf = (EndUser, "{tid}:{oid}")`, `Source = HostAsserted`, and `Scopes` = app scopes
   intersected with token scopes.
3. Present but invalid -> `401` with `code = "obo_token_invalid"`. **Never fall back to
   `AppOnly`.** A caller that asserts a user and gets it wrong must fail, not silently escalate to
   acting as the app.

Step 3 is the fail-closed rule that makes the whole design safe. It is the one place where a
tolerant implementation would create a privilege-escalation path.

**One gap to close in the same slice.** `ChatModesController` carries no `[InboundS2SAuth]`
attribute today (2.3). It must be added, or `/api/chat-modes` becomes an unauthenticated way to
read and write every tenant's modes once modes become per-user (#304).

### 4.3 Convergence point

Both doors produce a `Principal` and place it in `IPrincipalAccessor` before any controller action
runs. `IConversationStore`, `IWorkspaceStore`, `IChatModeStore`, `MultiTurnAgentPool`,
`UsageLedger`, and the authorization policy of section 7 see only `Principal`. None of them can
observe which door was used, except through the audit-only `Principal.Source`.

### 4.4 Tenant provisioning and the sign-in rejection path

> **Decision: tenants are explicitly provisioned.** An operator creates the tenant and names its
> first admin *before* anyone from that organisation can sign in. A first sign-in from an unknown
> Entra tenant is a **rejection**, never an auto-create.

Auto-creating a tenant on first sign-in would mean any person in any Entra tenant on earth could
mint themselves an organisation on our platform by clicking sign in. Provisioning is the commercial
onboarding step; it is deliberately manual.

**Provisioning surface: an operator API endpoint.**

```
POST /api/admin/tenants
X-S2S-Auth: <operator secret>
Content-Type: application/json

{
  "tenantId":      "tnt_acme",
  "entraTenantId": "<entra tid guid>",
  "displayName":   "Acme Corp",
  "firstAdminUpn": "dana@acme.example"
}
```

Chosen over the two alternatives on what the repo already has:

- **Config seed** would match the `FileChatModeStore` / `FileWorkspaceStore` precedent, but tenant
  creation is a per-customer onboarding event and a config seed makes every new customer a
  redeploy. Rejected for production; retained only as `Identity:SeedTenants`, applied idempotently
  at startup and **only when `Identity:Enforce=false`**, for development and single-tenant installs.
- **A CLI command** has no precedent - the sample ships no admin CLI, so this would be a new
  surface to build, document, and secure.
- **An API endpoint** reuses `[InboundS2SAuth]`, which already provides an authenticated
  non-user surface with a constant-time-compared operator secret (2.3).

**Why it cannot be authenticated as a tenant admin.** There is no admin until the tenant exists.
Tenant creation therefore sits on the *operator* trust boundary (`X-S2S-Auth`), not the Entra
sign-in boundary.

**Two traps in reusing `[InboundS2SAuth]` here, both of which must be closed explicitly:**

1. The guard is **marker-gated** (2.3) - `IsServiceToServiceRequest` returns false when neither
   `X-S2S-Auth` nor `X-Sbx-App-Id` is present, and the request passes through. On
   `TenantsController` that would let an unauthenticated browser request create a tenant. This
   controller must require the header **unconditionally**, not inherit the marker gate.
2. The guard **disables itself entirely** when `Auth:S2SInboundSecret` is blank. On this controller
   that must be a hard `503`, not an open door: tenant provisioning fails closed when no operator
   secret is configured.

**The sign-in rejection path.** A token that validates but whose `tid` has no `active` row in
`tenants`:

- **Response:** `403` with `{ "error": "tenant_not_provisioned", "code": "tenant_not_provisioned" }`.
  A suspended tenant gets the same shape with `code = "tenant_suspended"` so support can tell the
  two apart.
- **What the user sees:** a dedicated "your organisation is not set up yet" screen naming a contact
  route. Explicitly **not** a redirect back to Entra. A `401`-style re-authentication redirect would
  loop forever, because signing in again cannot fix a missing tenant - that loop is the specific
  failure mode this path exists to avoid.
- **What is logged:** one Warning-level `AuthenticationAuditRecord` (7.7) with
  `outcome = rejected`, `reason = unknown_tenant`, `claimedEntraTenantId`, `claimedObjectId`,
  `appId`, and the correlation id - plus `claimedUpn` only when `Identity:Audit:IncludeUpn` is set,
  since the person named there is by definition not our user. This record is a pre-principal one
  precisely because no principal exists: the rejection is the reason none was constructed. It is
  the signal an operator uses to notice that someone is waiting to be onboarded. The raw token is
  never logged. Records are deduplicated per `tid` over a short window so a client retry loop
  cannot flood the log.

**Binding the first admin.** On a user's first successful sign-in, if a `tenant_admins` row for that
tenant matches their lower-cased `preferred_username` and still has `user_id IS NULL`, `user_id` is
bound to `{tid}:{oid}` and `bound_at` is stamped. That principal then carries role `admin`. Once
bound, the UPN is never consulted again (8.2).

---

## 5. Token validation rules for the OBO JWT

Fail closed at every step. Every failure produces an audit record and no principal. The status code
is **not** uniform, and the rule is stated here once so the per-step codes below are not read as
inconsistencies:

- **`401 Unauthorized`** - the token could not be established as genuine: bad `typ`, disallowed
  algorithm, wrong issuer or audience, bad signature, expired, missing `oid`/`jti`, or replayed.
  The caller may retry with a better token.
- **`403 Forbidden`** - the token is genuine and its claims are trusted, but the identity it names
  is not entitled to be here: an unprovisioned or suspended tenant (step 7), or an app asserting a
  user outside its own tenant (step 11). Retrying with a fresh token changes nothing; an operator
  has to act.

Steps 1-6 and 8-10 are `401`. Steps 7 and 11 are `403`. Neither response body distinguishes further
- both return only a stable code (`invalid_token`, `tenant_not_provisioned`, `tenant_mismatch`) with
no detail about which tenants exist.

### 5.1 Validation sequence

Ordered, and short-circuiting:

1. **Header type.** `typ` must be `at+jwt` or `application/at+jwt` (RFC 9068). Tokens we mint
   (embed tokens, section 6) conform. Note that **Entra's own v2.0 access tokens emit `typ: JWT`,
   not `at+jwt`** - so this check applies only to host-minted OBO JWTs and to our own tokens, never
   to a raw Entra token forwarded unchanged.
2. **Algorithm allow-list.** `RS256`, `ES256`, `PS256`. Reject `none`. Reject all symmetric
   algorithms (`HS*`) for a federated issuer - a symmetric key would make the verifier able to mint.
3. **Issuer.** `iss` must exactly equal the issuer registered for the presenting app id. For a
   multi-tenant Entra issuer, the discovery document's `issuer` is the *template*
   `https://login.microsoftonline.com/{tenantid}/v2.0`; `{tenantid}` is substituted with the
   token's own `tid` before comparison. `Microsoft.Identity.Web` does this; a hand-rolled
   `IssuerValidator` must replicate it.
4. **Audience.** `aud` must equal the configured audience for this API. For an Entra-issued
   assertion this is the API's client id - v2.0 tokens carry the client id GUID in `aud`, not a
   resource URI.
5. **Signature** against the key identified by `kid` (5.2).
6. **Lifetime.** `nbf` and `exp` with the clock skew of 5.3.
7. **Tenant resolution.** `tid` must be present, and looking it up as
   `tenants.entra_tenant_id = tid` must return exactly one row with `status = 'active'`. That row's
   `tenant_id` - our internal `tnt_*` value - is what becomes `Principal.TenantId`; the raw `tid`
   GUID is never used as a tenant id anywhere past this line. A well-signed token whose `tid` has
   no row is `403 tenant_not_provisioned`; a row with `status = 'suspended'` is `403` as well.
   Resolution never creates a tenant (4.4). Call the resolved value `resolvedTenantId` for step 11.
8. **Subject.** `oid` must be present; the user id is `{tid}:{oid}` (3.3). This key deliberately
   uses the **Entra** `tid`, not the internal id, because it must stay stable if a tenant is ever
   re-provisioned under a new internal id.
9. **Replay.** `jti` must be present and unseen (5.4).
10. **Actor chain.** If `act` is present, record it into `DelegationChain` (5.5).
11. **Tenant agreement.** `resolvedTenantId` from step 7 must equal the internal tenant id the
    presenting app id is onboarded to (`Identity:Apps:<appId>:TenantId`, 5.2). **Both sides of this
    comparison are internal `tnt_*` ids** - comparing the token's raw `tid` GUID against the
    configured internal id would never match, and the check would either reject everything or, if
    an implementer "fixed" it by removing the check, silently drop cross-tenant containment. A
    mismatch means an app registered for tenant A is asserting a user of tenant B - reject with
    `403 tenant_mismatch` and raise it as a security event, not a routine denial.

Step 11 is the cross-tenant containment check. Without it, a single compromised app credential
could assert users in every tenant. Note the two steps do different work: step 7 asks "is this
Entra tenant a customer of ours?", step 11 asks "is it *this app's* customer?".

### 5.2 Key distribution and rotation

**Preferred: per-app JWKS URI.** Each onboarded app registration records:

```json
"Identity": {
  "Apps": {
    "acme-portal": {
      "TenantId": "tnt_acme",
      "Issuer": "https://login.microsoftonline.com/{tenantid}/v2.0",
      "Audience": "<api client id>",
      "JwksUri": "https://login.microsoftonline.com/<tenant>/discovery/v2.0/keys"
    }
  }
}
```

`TenantId` here is our internal tenant id (3.3) - a `tnt_*` value that must already exist in the
`tenants` table (8.2), so an app registration cannot be onboarded to a tenant nobody provisioned.
It is compared in step 11 of 5.1 against `resolvedTenantId`, the *internal* id that step 7 obtained
by looking up the token's `tid`. The configuration never contains an Entra `tid` GUID; the
`tid` -> `tnt_*` mapping lives in exactly one place, the `tenants.entra_tenant_id` column.

Startup validation: every `Identity:Apps:*:TenantId` is resolved against the `tenants` table when
the host starts. An unresolvable value is a **startup failure**, not a runtime `403` - a typo in an
app registration should not present as an authentication bug months later.

`jwks_uri` is resolved from the issuer's OIDC discovery document at
`{issuer}/.well-known/openid-configuration` rather than hardcoded, because the path is not
guaranteed to be stable across issuers.

**Caching, per Microsoft's signing-key-rollover guidance:** cache each key individually by `kid`;
TTL **24 hours** per key; background refresh every **1 hour**; on an unrecognised `kid`, refresh
opportunistically but rate-limited to **at most once per 5 minutes** so an attacker cannot use
unknown-`kid` tokens to drive unbounded outbound requests. Cache **per tenant**, not globally, even
for a multi-tenant app - keys that look shared today may diverge per tenant later.
`Microsoft.IdentityModel.Protocols.ConfigurationManager<T>` - which `JwtBearerHandler` uses
internally - implements this; prefer it to a hand-rolled cache.

**Fallback for hosts that cannot serve JWKS:** a registered static public key, with **two slots**
(`Primary`, `Secondary`) both accepted during an overlap window. This is the dual-key rotation
model already recorded in `docs/adrs/0005-service-to-service-lifecycle-delivery.md` - reuse it
rather than inventing a second rotation story. Keys are write-only: set and rotated, never read
back, per the never-log invariant in `docs/deployment/AUTH_ENFORCE.md`.

### 5.3 Clock skew

**120 seconds**, set explicitly:

```csharp
options.TokenValidationParameters.ClockSkew = TimeSpan.FromSeconds(120);
```

The ASP.NET Core default is `TokenValidationParameters.DefaultClockSkew` = **300 seconds**, which
is too generous for tokens with a 5-15 minute intended lifetime - it would extend a 5-minute
token's acceptance window by a full 100%. 120s matches the figure already agreed in #237.

### 5.4 Replay

`jti` is required. A validated `jti` is recorded with an expiry of `exp + ClockSkew` and rejected
on second presentation. A replayed `jti` is `401` **and** an `AuthenticationAuditRecord` (7.7) with
`eventClass = security` and `reason = replayed_jti`, not a routine denial - a replay is evidence,
and a log line that looks like an ordinary `401` will not be noticed. The record carries the `jti`
so the replay can be correlated with the original accepted use.

**Store.** Single-instance today: an in-memory expiring set. This is honestly insufficient the
moment the API runs more than one replica, because a replay directed at a second instance would
succeed. The durable, shared implementation depends on the coordination core (lease, fence, CAS
store) delivered by [#236](https://github.com/achieveai/LmDotnetTools/issues/236). Until #236 lands,
a multi-instance deployment must either use sticky routing or accept the gap explicitly.

> **Deployment limitation - read this before adding a replica.** The replay defence is held in
> process memory. A second replica behind a load balancer does not weaken it a little; it removes
> it, because the attacker's replay simply needs to land on the instance that has not seen the
> `jti`. Until #236's coordination core provides a shared CAS store, a deployment running more than
> one replica MUST either pin a caller to one instance or accept that OBO tokens are replayable
> within their lifetime.

So that this cannot be missed by someone who never reads this document, the replay store reports
itself at runtime. Concretely:

- A new `GET /api/diagnostics/identity` action on the existing
  `samples/LmStreaming.Sample/Controllers/DiagnosticsController.cs:14` (route prefix
  `api/diagnostics`, alongside the existing `provider-info` and `serialization-samples` actions)
  returns `{ "enforce": true|false, "replayStore": "in-memory"|"shared", "replicaSafe": false|true }`.
  Reusing this controller rather than adding another one keeps the operator's diagnostic surface in
  one place; it is registered like the rest of the sample's controllers and needs no new wiring.
- Startup logs exactly one `Warning` when `replayStore` is `in-memory`, naming this section.

A configuration flag alone would not do - the operator adding a replica is rarely the person who
configured identity. The action ships in slice 5 with the rest of the operator surface (11).

### 5.5 The `act` claim

RFC 8693 represents a delegation chain by nesting `act` inside `act`, outermost = current actor:

```json
"act": {
  "sub": "https://service16.example.com",
  "act": { "sub": "https://service77.example.com" }
}
```

Mapping: the outermost `act.sub` becomes `Principal.Actor`; nested entries become
`DelegationChain` in outermost-first order. Per the RFC, only top-level claims and the current
actor participate in an access decision - `DelegationChain` is audit only.

An `act` claim signals **delegation**, not impersonation: the delegate keeps its own identity and
it is explicit that actions are taken by A representing B. That is exactly the semantics we want,
and it is why `Actor` and `OnBehalfOf` are separate fields rather than one collapsed "user".

`may_act` - a forward-looking statement that a party is *permitted* to become an actor - is not
consumed in P1. Authorization to act for a user is decided by the app-registration onboarding
record, not by a claim the caller supplies.

### 5.6 Accepted token-minting shapes

Where a host runs its own STS, the RFC 8693 exchange request is:

```
grant_type=urn:ietf:params:oauth:grant-type:token-exchange
subject_token=<the end user's token>
subject_token_type=urn:ietf:params:oauth:token-type:access_token
audience=<our API audience>
scope=<space-delimited>
requested_token_type=urn:ietf:params:oauth:token-type:jwt
```

with optional `actor_token` / `actor_token_type` when the acting party is itself token-identified.
The response carries `access_token`, `issued_token_type`, `token_type`, `expires_in`, and `scope`.

Where the host instead uses Entra's own on-behalf-of flow to mint a token for our API, it posts to
`https://login.microsoftonline.com/{tenant}/oauth2/v2.0/token`:

```
grant_type=urn:ietf:params:oauth:grant-type:jwt-bearer
requested_token_use=on_behalf_of
assertion=<the token that was sent to the host>
client_id=<host client id>
client_secret=<host secret>
scope=<our API scope>
```

or, with a certificate instead of a secret, replacing `client_secret` with:

```
client_assertion_type=urn:ietf:params:oauth:client-assertion-type:jwt-bearer
client_assertion=<JWT signed with the registered certificate>
```

Two Entra constraints matter to integrators: the `assertion` must carry `aud` equal to the
requesting app's own client id, and OBO works only for delegated (user) tokens - an app-only token
must use client credentials instead, which yields `Source = AppOnly` and no human.

We accept either shape. Validation (5.1) is identical; only the issuer registration differs.

---

## 6. Embed token - mode A

### 6.1 Who mints it

**The host's backend, by calling our API.** The browser never mints an embed token and never talks
to Entra for this flow.

```
POST /api/embed/tokens
X-Sbx-App-Id: <app id>
X-Sbx-App-Key: <app key>
X-S2S-Auth: <inbound secret>
Authorization: Bearer <OBO JWT naming the end user>
Content-Type: application/json

{ "resource": "conversation:thr_abc123", "scopes": ["conversation.read", "conversation.write"] }
```

The request is authenticated exactly as section 4.2 - this endpoint mints nothing without a valid
app credential *and* a valid OBO JWT. The minted token can never exceed the intersection of the
two (3.2).

Response:

```json
{ "token": "<jwt>", "expiresIn": 900, "resource": "conversation:thr_abc123" }
```

### 6.2 Claims

Minted by us, RFC 9068-conformant, header `typ: at+jwt`, signed `RS256`:

| Claim | Value |
|---|---|
| `iss` | Our issuer, e.g. `https://<host>/identity` |
| `aud` | `lmstreaming-embed` - a dedicated audience, so an embed token is never accepted on the general API |
| `sub` | `{tid}:{oid}` of the end user |
| `tid` | Tenant id |
| `azp` | App id that minted it |
| `res` | **Exactly one** resource: `conversation:{threadId}` or `workspace:{workspaceId}` |
| `scope` | Space-delimited, subset of the minting principal's scopes |
| `jti` | Replay id |
| `iat`, `nbf`, `exp` | `exp - iat <= 900` seconds |
| `client_id` | REQUIRED by RFC 9068 section 2.2; the app id |

### 6.3 Lifetime

**15 minutes maximum, and never longer than the remaining lifetime of the OBO JWT that authorised
the mint.** Deriving the ceiling from the authorising token is the Power BI embed-token rule and it
matters: a long-lived embed token minted from an about-to-expire user assertion would outlive the
evidence that the user was ever present.

Renewal is a fresh `POST /api/embed/tokens` from the host backend. There is no refresh token.

### 6.4 How the iframe receives it

**Via `postMessage`, never in the frame `src` query string.**

```
host page                                   iframe (our origin)
  |-- <iframe src="https://<host>/embed"> ----->|
  |<---------- postMessage {type:"embed-ready"} |
  |-- postMessage {type:"embed-token", token} ->|
  |                            (held in memory) |
  |                  Authorization: Bearer ---->| API
```

Both sides pass an explicit `targetOrigin` to `postMessage` and both verify `event.origin` on
receipt. A wildcard `"*"` targetOrigin is a defect, not a shortcut.

**Why not the query string.** A URL is recorded in browser history, in server and proxy access
logs, in APM traces, and is emitted in the `Referer` header on the next outbound request. None of
that applies to a value that is never in a URL. This is the OWASP position on tokens in query
strings and it is not negotiable for a credential.

Token refresh is a further `postMessage` from the host before expiry; the iframe is never reloaded
to change tokens.

### 6.5 Framing controls

Served on the `/embed` route:

- `Content-Security-Policy: frame-ancestors <the app registration's allowed origins>` - the
  authoritative control. Origins come from the app registration, not from a request parameter.
- `X-Frame-Options: DENY` on every non-embed route. It is a legacy fallback only: any browser
  supporting CSP obeys `frame-ancestors` and ignores `X-Frame-Options`, so `frame-ancestors` is
  the real policy.
- The host is advised to set
  `sandbox="allow-scripts allow-same-origin allow-forms allow-popups"`.

  **`allow-same-origin` is required, not optional.** Without it the framed document is assigned an
  *opaque* origin, and three things in 6.4 stop working:
  1. The iframe's `postMessage` arrives at the host with `event.origin === "null"`, so the host
     cannot verify who sent `embed-ready` - and 6.4 requires it to.
  2. The host has no non-wildcard `targetOrigin` to send the token to, since `"null"` is not a
     usable target. The only way to deliver the token becomes `targetOrigin: "*"`, which 6.4 calls
     a defect.
  3. The framed app cannot read its own origin's storage, and same-origin XHR/fetch to our API is
     treated as cross-origin from an opaque origin.

  The well-known warning that `allow-scripts` plus `allow-same-origin` lets a document remove its
  own `sandbox` attribute applies **only when the framed content is same-origin with the parent** -
  the escape works by reaching into the parent document. The embed is served from our origin and
  the host page from theirs, so the pairing is safe here. This is called out because a reviewer
  applying the rule mechanically will ask for `allow-same-origin` to be dropped, and dropping it
  silently forces a wildcard `targetOrigin`.

  The sandbox is defence in depth regardless; `frame-ancestors` above is the control that actually
  bounds who may frame us.
- **No cookies are used by the embed**, which is what avoids `SameSite=None; Secure` and CHIPS
  `Partitioned` entirely. This is the main practical argument for the bearer-token choice in 4.1.

### 6.6 Scoping to one resource

The `res` claim names exactly one conversation or workspace. The embed-token validation filter
rejects any request whose route resource does not match `res`, before the authorization policy of
section 7 runs. An embed token therefore cannot list conversations, cannot enumerate workspaces,
and cannot reach a second conversation - not even one the same user owns. Mode A is deliberately
the narrowest door.

---

## 7. Authorization model

### 7.1 Principles

1. **Private by default.** A resource is visible to its owner and to no one else until an explicit
   grant is created.
2. **Named users only.** A grant names a user id. No link sharing, no org-wide-by-default.
3. **Tenant is an outer boundary, not a permission.** Every query filters on `tenant_id` first. A
   cross-tenant read is impossible by construction, not by policy.
4. **Absent means denied, and null never matches.** A resource with no owner is not public. Any
   comparison in which either side is null - a null `OwnerUserId`, or a principal with a null
   `EffectiveUserId` - is **not a match**, never a match. This is stated as a rule because the
   obvious implementation (`a == b` on two nullable strings) gets it exactly backwards: it makes
   every unowned resource match every app-only principal. See 7.4 step 3 and the migration policy
   in 8.5.
5. **Not-found, not forbidden.** A read of a resource in another tenant, or one the principal has
   no grant on, returns `404`. This matches the gateway's existing uniform-404 cross-app behaviour
   and avoids confirming that an id exists. A *write* to a resource the principal can read but not
   modify returns `403`, because existence is already established.

### 7.2 Resource types and actions

| Resource type | Id | Actions |
|---|---|---|
| `conversation` | `threadId` | `read`, `write`, `delete`, `share` |
| `workspace` | `workspaceId` | `read`, `use`, `write`, `delete`, `share` |
| `mode` | `modeId` | `read`, `use`, `write`, `delete`, `publish` |

`use` is deliberately separate from `read`: seeing that a workspace exists and being able to run an
agent inside it are different privileges, and conflating them is how access silently widens.
`publish` on a mode is the admin-only action that turns a private mode into a tenant-shared one.

### 7.3 Roles

Flat, tenant-scoped:

- `member` - default for every signed-in user.
- `admin` - may publish shared modes, view the tenant usage report (section 9), and read any
  resource in the tenant for support purposes. Every admin read is audited.

### 7.4 The decision point

New in `src/LmCore/Identity/`:

```csharp
/// <summary>Addresses a resource: what kind, and which one.</summary>
public readonly record struct ResourceRef(string Type, string Id);

/// <summary>
/// The ownership facts the policy needs about one resource. Loaded by the caller from whichever
/// store owns the resource, so the policy performs no I/O of its own and is directly unit-testable.
/// </summary>
public sealed record ResourceDescriptor
{
    public required ResourceRef Ref { get; init; }

    /// <summary>Owning tenant. Ignored when <see cref="IsSystemDefined"/> is true.</summary>
    public required string TenantId { get; init; }

    /// <summary>Owning end user, '{tid}:{oid}'. Null for an app-owned or legacy resource.</summary>
    public string? OwnerUserId { get; init; }

    /// <summary>Owning app id. Null for a resource created through the interactive UI.</summary>
    public string? OwnerAppId { get; init; }

    /// <summary>Read-only built-in - a system mode or the seeded workspace.</summary>
    public bool IsSystemDefined { get; init; }
}

public enum AccessAction { Read, Use, Write, Delete, Share, Publish }

public sealed record AccessDecision(bool Allowed, string Reason)
{
    public static AccessDecision Deny(string reason)  => new(false, reason);
    public static AccessDecision Allow(string reason) => new(true, reason);
    public static readonly AccessDecision AllowOwner    = new(true, "owner");
    public static readonly AccessDecision AllowGrant    = new(true, "grant");
    public static readonly AccessDecision AllowAdmin    = new(true, "tenant_admin");
    public static readonly AccessDecision AllowSystem   = new(true, "system_defined");
    public static readonly AccessDecision AllowDisabled = new(true, "enforcement_disabled");
}

public interface IResourceAccessPolicy
{
    ValueTask<AccessDecision> EvaluateAsync(
        Principal principal,
        ResourceDescriptor resource,
        AccessAction action,
        CancellationToken ct = default);
}
```

Named `IResourceAccessPolicy`, not `IAuthorizationService`, to avoid colliding with the ASP.NET
Core interface of that name.

Evaluation order, first match wins:

0. **Enforcement off.** When `Identity:Enforce` is false (4.1) -> `AllowDisabled`, audited. This is
   the pre-rollout path and the reason existing tests keep passing; it is deliberately the first
   step, so that nothing below it is ever exercised with the development principal and no rule can
   be misread as the disabled behaviour.
1. `resource.IsSystemDefined` and action is `Read` or `Use` -> `AllowSystem`. System-defined
   resources are readable by every member of every tenant and writable by no one, so this is
   evaluated **before** the tenant check and `TenantId` is not consulted for them.
2. `resource.TenantId != principal.TenantId` -> `Deny("cross_tenant")`. Admins do not bypass this;
   a tenant admin is an admin of exactly one tenant.
3. **Subject resolution.** Let `user = principal.EffectiveUserId`.
   - If `user is null` the caller is app-only (mode C, or a service acting as itself). It may match
     **only** on the app dimension: `resource.OwnerAppId is not null && resource.OwnerAppId ==
     principal.AppId` -> `AllowOwner`; otherwise -> `Deny("app_only_no_owner")`. An app-only
     principal never matches a null owner and never consults grants.
   - Otherwise continue to step 4.
4. `resource.OwnerUserId is not null && resource.OwnerUserId == user` -> `AllowOwner`.
5. An unexpired grant exists for `(principal.TenantId, resource.Ref, user)` conferring the action ->
   `AllowGrant`.
6. `principal.Roles` contains `admin` and the action is permitted for admins -> `AllowAdmin`,
   audited.
7. Otherwise -> `Deny("no_grant")`.

**The null rule (7.1 principle 4) is load-bearing in steps 3 and 4.** Both `OwnerUserId` and
`EffectiveUserId` are nullable. Writing step 4 as a bare `resource.OwnerUserId == user` would make a
legacy row with no owner match every app-only principal, silently handing unowned data to any
service credential - the precise opposite of "private by default". The non-null guards are not
defensive style; they are the rule.

SQL behaves correctly here without the guard, because `NULL = 'x'` is `NULL` rather than true. C#
`==` on two nulls is `true`. That asymmetry between 7.5's query and this algorithm is exactly where
an implementer is likely to go wrong, so both are written out explicitly.

`AccessDecision.Reason` is written to the audit record for allows as well as denies. A deny-only
audit cannot answer "was this ever attempted successfully?".

### 7.5 Listing is a filter, not a loop

Listing endpoints must not fetch-then-filter. `IConversationStore.ListThreadsAsync` gains a
principal parameter and pushes the predicate into SQL:

The predicate must mirror **every** allow branch of 7.4, not just the owner branch. A query that
omits the admin branch produces the worst possible outcome: `GET /conversations` returns an empty
list for a tenant admin while `GET /conversations/{id}` on the same rows returns `200`. The list and
the point read must agree.

Four parameters are bound from the principal before the query runs:

| Parameter | Value |
|---|---|
| `@tenantId` | `principal.TenantId` |
| `@userId` | `principal.EffectiveUserId`, may be `NULL` for an app-only caller |
| `@appId` | `principal.AppId`, `NULL` for an interactive caller |
| `@isTenantAdmin` | `1` when `principal.Roles` contains `admin`, else `0` - computed once, not per row |

```sql
SELECT ... FROM thread_metadata t
WHERE t.tenant_id = @tenantId
  AND ( @isTenantAdmin = 1
        OR (@userId IS NOT NULL AND t.owner_user_id = @userId)
        OR (@userId IS NULL AND @appId IS NOT NULL AND t.owner_app_id = @appId)
        OR (@userId IS NOT NULL AND EXISTS (
              SELECT 1 FROM resource_grants g
              WHERE g.tenant_id     = @tenantId
                AND g.resource_type = 'conversation'
                AND g.resource_id   = t.thread_id
                AND g.subject_id    = @userId
                AND (g.expires_at IS NULL OR g.expires_at > @now))) )
ORDER BY t.last_updated DESC
LIMIT @limit OFFSET @offset;
```

The `@userId IS NOT NULL` guards are the SQL spelling of 7.4 step 3: without them an app-only
principal would fall through to the grant sub-query with a `NULL` subject. They do not protect
against a `NULL` **owner** - SQL already handles that, since `NULL = @userId` evaluates to `NULL`
and never satisfies the `WHERE`.

Listing a tenant admin's results emits one audit record for the query, not one per row: action
`read`, resource type `conversation`, resource id `*`, reason `tenant_admin`.

Workspaces and modes use the identical three-branch shape. Their stores are whole-file JSON today
(2.6) and so filter in memory, which is safe only because they have no `LIMIT`; if OQ-3 moves them
into SQLite they must adopt the query form above rather than keep the in-memory filter.

In-memory filtering after a `LIMIT` would silently return short pages - the page would be trimmed
after the database had already truncated it.

Separately, `ListThreadsAsync(limit, offset, ct)`
(`src/LmMultiTurn/Persistence/IConversationStore.cs:115`) paginates by offset over
`last_updated DESC`, a **mutable** sort key: a conversation touched between page 1 and page 2 moves
and a row is skipped. Adding the owner filter does not cause this, but it does make it more
visible. Recorded as OQ-2.

### 7.6 The pool guard gains a principal dimension

`MultiTurnAgentPool`'s app-id freeze is **kept exactly as it is** - it is the tenancy boundary and
removing it would change gateway ownership semantics. A second, parallel check is added on
`OwnerPrincipal.EffectiveUserId`, throwing a new `PrincipalConflictException` on mismatch, mapped
to `409` with `code = "principal_conflict"` alongside the existing `caller_credential_conflict`
(`ConversationsController.cs:759`).

This is what makes the guard mean something in the UI, where today both sides are `null` and
therefore always match (2.2).

### 7.7 Audit records

> **Decision: P1 writes audit records to the existing structured logs. They migrate to P4's durable
> outbox when that exists.**

#237 routes audit through P4's outbox. P4 does not exist, and blocking every authorization decision
in P1 on a pillar that has not started would be the wrong trade. The interim costs nothing to build:
the repository already runs Serilog with `CompactJsonFormatter` writing structured JSONL, and those
logs are already queried with DuckDB (see `CLAUDE.md`).

**What makes the later migration mechanical** is that the interim must not be ad-hoc logging at call
sites. Every record goes through one sink.

**There are two record kinds, and conflating them does not work.** The events that most need an
audit trail - an unprovisioned tenant rejected at sign-in (4.4), a cross-tenant token (5.1 step 11),
a replayed `jti` (5.4), a signature that did not verify - all occur *before* a `Principal` exists,
by definition: they are the reasons no principal was constructed. A single record type whose fields
are sourced from `Principal` therefore cannot carry them, and an implementer forced to fill
`actorId` would either invent a placeholder or, worse, skip the record. So:

```csharp
public interface IAuditSink
{
    void Write(AuthenticationAuditRecord record);   // pre-principal
    void Write(AuthorizationAuditRecord record);    // post-principal
}
```

Both are emitted under a fixed `SourceContext` of `Audit` and share `eventId`, `timestamp`,
`correlationId`, `eventClass`, `outcome`, and `reason`, so one DuckDB query over `SourceContext =
'Audit'` returns both and `recordKind` discriminates.

**`AuthenticationAuditRecord`** - written by the authentication handlers (5.1, 4.4, 6.x), where the
only identity available is what the presented token *claimed*:

| Field | Source |
|---|---|
| `recordKind` | constant `authentication` |
| `eventId` | new GUID |
| `timestamp` | `TimeProvider.GetUtcNow()` |
| `frontDoor` | `interactive` \| `s2s_obo` \| `embed` |
| `claimedEntraTenantId` | the raw `tid` claim, or null if the token did not parse |
| `claimedObjectId` | the raw `oid` claim, or null |
| `claimedUpn` | `preferred_username`, **only when `Identity:Audit:IncludeUpn` is true** |
| `appId` | the presented `X-Sbx-App-Id`, or null |
| `resolvedTenantId` | our internal `tnt_*` if resolution succeeded, else null |
| `jti` | the token's `jti`, for correlating a replay with its original use |
| `outcome` | `accepted` \| `rejected` |
| `reason` | the stable failure code (`unknown_tenant`, `tenant_mismatch`, `replayed_jti`, ...) |
| `correlationId` | ambient request correlation id |
| `eventClass` | `routine` or `security` |

Note what is **not** here: no token, no signature, no bearer value, ever - the never-log invariant
of `docs/deployment/AUTH_ENFORCE.md` applies unchanged. `claimedUpn` is behind a flag because a
rejected sign-in is exactly the case where the presented identifier belongs to someone who is not
our user; some deployments will want it for support, some will not want it retained at all.

**`AuthorizationAuditRecord`** - written by `IResourceAccessPolicy` (7.4) and by the admin listing
path (7.5), where a `Principal` exists by construction:

| Field | Source |
|---|---|
| `recordKind` | constant `authorization` |
| `eventId` | new GUID per decision |
| `timestamp` | `TimeProvider.GetUtcNow()` |
| `actorKind`, `actorId` | `Principal.Actor` |
| `onBehalfOfKind`, `onBehalfOfId` | `Principal.OnBehalfOf`, null when absent |
| `tenantId`, `appId` | `Principal` |
| `source` | `Principal.Source` |
| `permission` | the `AccessAction` |
| `resourceType`, `resourceId` | the `ResourceRef` (`*` for a listing) |
| `outcome` | `allow` or `deny` |
| `reason` | `AccessDecision.Reason` (7.4) |
| `correlationId` | ambient request/run correlation id |
| `eventClass` | `routine` or `security` |

Neither record's field set may be extended or trimmed at a call site. Migrating to P4 then means
reimplementing `IAuditSink` against the outbox and changing nothing else; the two record kinds
become two outbox event types.

Two rules carried over from #237 unchanged: **both allows and denies are recorded** - a deny-only
trail cannot answer "was this ever attempted successfully?" - and records are **not redacted at
rest**, because redaction is applied per viewer at read time and storing pre-redacted audit destroys
the record an incident review needs.

**The honest limitation.** Log retention is an operational setting, not an audit retention
guarantee, and structured logs are rotated and archived on a schedule chosen for debugging rather
than for compliance. Until P4's outbox lands, this is a diagnostic-grade trail and must not be
described to a customer as a compliance-grade one.

---

## 8. Data model changes

### 8.1 A migration mechanism must exist first

There is none (2.5). Before any column can be added, `SqliteSchemaInitializer` needs a versioned
migration runner. Recommendation: `PRAGMA user_version`, an ordered array of migration steps, each
applied in a single transaction with `user_version` bumped in that same transaction.

The existing `CREATE TABLE IF NOT EXISTS` block becomes migration step 1, so a database created by
an earlier build is recognised as already at version 1 without re-running DDL.

**Step number == the `user_version` the database holds after the step commits.** This table is the
single source of truth for those numbers; anywhere else in this document that names a version must
agree with it.

| Step | `user_version` after | Adds | Section | Slice |
|---|---|---|---|---|
| 1 | 1 | the existing eight tables, unchanged | 2.5 | - (already deployed) |
| 2 | 2 | `tenants`, `tenant_admins` | 8.2 | 1 (#301) |
| 3 | 3 | owner columns on `thread_metadata`; quarantine tenant row; backfill | 8.3, 8.5 | 2 (#302) |
| 4 | 4 | `resource_grants` | 8.4 | 2 (#302) |
| 5 | 5 | `usage_rollup` | 9.2 | 6 (#306) |

So slice 1 takes a database from 1 to 2, slice 2 from 2 to **4**, and slice 6 from 4 to 5. A fresh
database created by the current build runs steps 1-5 in order and lands at 5; the runner never
branches on "new versus existing".

The runner is built in slice 1 rather than slice 2, because the `tenants` table needed by explicit
provisioning (4.4) is itself a migration step.

File changed: `src/LmMultiTurn/Persistence/Sqlite/SqliteSchemaInitializer.cs`.

### 8.2 New tables: `tenants` and `tenant_admins`

Tenants are **explicitly provisioned** (4.4), so these tables exist from slice 1 - they are the
thing every other ownership column points at.

```sql
-- migration step 2
CREATE TABLE IF NOT EXISTS tenants (
    tenant_id       TEXT PRIMARY KEY,   -- our stable internal id, e.g. 'tnt_acme'
    entra_tenant_id TEXT,               -- Entra 'tid' GUID; NULL only for the legacy tenant
    display_name    TEXT NOT NULL,
    status          TEXT NOT NULL,      -- 'active' | 'suspended'
    created_at      INTEGER NOT NULL,
    created_by      TEXT NOT NULL       -- operator identifier from the provisioning call
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_tenants_entra
  ON tenants (entra_tenant_id) WHERE entra_tenant_id IS NOT NULL;

CREATE TABLE IF NOT EXISTS tenant_admins (
    tenant_id  TEXT NOT NULL,
    upn        TEXT NOT NULL,           -- lower-cased UPN, known before first sign-in
    user_id    TEXT,                    -- '{tid}:{oid}', NULL until first sign-in binds it
    granted_at INTEGER NOT NULL,
    granted_by TEXT NOT NULL,
    bound_at   INTEGER,
    PRIMARY KEY (tenant_id, upn)
);
CREATE INDEX IF NOT EXISTS ix_tenant_admins_user ON tenant_admins (user_id);
```

**The Entra-to-internal mapping is `tenants.entra_tenant_id` -> `tenants.tenant_id`.** Sign-in reads
the token's `tid`, finds the row, and puts `tenant_id` into `Principal.TenantId` (3.3). The partial
unique index makes it impossible for two tenants to claim the same Entra directory, while still
permitting the one legacy row that has no Entra directory behind it at all (8.5).

`entra_tenant_id` is nullable for exactly that reason - the legacy tenant predates Entra. Every
*provisioned* tenant has one.

**Why `tenant_admins` is keyed by UPN, not by user id.** The first admin must be named *before* they
have ever signed in, so their `oid` is not yet knowable. The operator supplies a UPN or verified
email; on that user's first successful sign-in the row's `user_id` is bound from `{tid}:{oid}` and
`bound_at` is stamped. Matching uses the lower-cased `preferred_username` claim and happens exactly
once. This is the only place `preferred_username` is trusted, and it is trusted only to bind - it is
never the durable key, because it is mutable (3.3).

### 8.3 New columns on `thread_metadata`

```sql
-- migration step 3
ALTER TABLE thread_metadata ADD COLUMN tenant_id     TEXT;
ALTER TABLE thread_metadata ADD COLUMN owner_user_id TEXT;
ALTER TABLE thread_metadata ADD COLUMN owner_app_id  TEXT;
CREATE INDEX IF NOT EXISTS idx_thread_metadata_owner
  ON thread_metadata (tenant_id, owner_user_id, last_updated DESC);
```

Nullable, because SQLite cannot add a `NOT NULL` column without a default and because null is the
signal for "legacy, unclaimed" (8.5). The index exactly matches the `WHERE` and `ORDER BY` of 7.5.

`owner_app_id` records which app created the conversation - it is the durable form of the
`CallerCredential` freeze, which today exists only in the in-memory pool and is lost on restart.
This also closes [#162](https://github.com/achieveai/LmDotnetTools/issues/162) (bind S2S ownership
at `Provision` rather than at first `SendMessage`), because `POST /api/conversations`
(`ConversationsController.cs:221`) can now write ownership at provision time.

Corresponding fields are added to `ThreadMetadata`
(`src/LmMultiTurn/Persistence/ThreadMetadata.cs`) as first-class properties - **not** into
`Properties`, which is serialized into `metadata_json` and cannot be filtered in SQL.

### 8.4 New table: `resource_grants`

```sql
-- migration step 4
CREATE TABLE IF NOT EXISTS resource_grants (
    tenant_id     TEXT NOT NULL,
    resource_type TEXT NOT NULL,          -- 'conversation' | 'workspace' | 'mode'
    resource_id   TEXT NOT NULL,
    subject_id    TEXT NOT NULL,          -- '{tid}:{oid}' of the grantee
    role          TEXT NOT NULL,          -- 'viewer' | 'editor'
    granted_by    TEXT NOT NULL,
    granted_at    INTEGER NOT NULL,
    expires_at    INTEGER,                -- NULL = no expiry
    PRIMARY KEY (tenant_id, resource_type, resource_id, subject_id)
);
CREATE INDEX IF NOT EXISTS idx_resource_grants_subject
  ON resource_grants (tenant_id, subject_id, resource_type);
```

`viewer` maps to `read` + `use`; `editor` adds `write`. Neither confers `delete` or `share` - those
stay with the owner. `expires_at` is present from the start because P3 takeover and P2 assist
claims both need time-boxed grants, and retrofitting expiry onto a grant table already in use is
painful.

One table serves all three resource types, so #302, #303, and #304 share the sharing UI, the policy
code, and the audit shape rather than growing three near-identical mechanisms.

### 8.5 Migration of existing rows

The hard question: what tenant and owner do conversations that already exist get?

**They go into a quarantine tenant, owned by nobody, and stay invisible until an operator adopts
them.** Not "a tenant that admins can browse" - that idea does not survive contact with explicit
provisioning, for the reason spelled out below.

#### 8.5.1 The backfill

Migration step 3, in one transaction:

```sql
-- 1. the quarantine tenant must exist before anything points at it
INSERT OR IGNORE INTO tenants
       (tenant_id,       entra_tenant_id, display_name,
        status,          created_at,      created_by)
VALUES (@legacyTenantId, NULL,            'Unadopted (pre-identity) data',
        'quarantined',   @now,            'migration');

-- 2. stamp every pre-existing row with that same id
UPDATE thread_metadata SET tenant_id = @legacyTenantId WHERE tenant_id IS NULL;
```

`@legacyTenantId` is resolved **once**, before the transaction opens, from
`Identity:LegacyTenantId` with a default of `"legacy"`, and the same variable is bound to both
statements. It must not be a literal in one statement and a parameter in the other: an operator who
sets `Identity:LegacyTenantId` would then create a row named `legacy` while the backfill stamped
rows with their configured value, and every migrated conversation would reference a tenant that
does not exist.

`owner_user_id` and `owner_app_id` stay `NULL`, meaning **unclaimed**.

#### 8.5.2 Why the quarantine tenant is invisible, deliberately

The quarantine tenant has no `entra_tenant_id`, so step 7 of 5.1 can never resolve a token to it,
so **no principal can ever carry `TenantId = 'legacy'`**. Its `status` is `quarantined`, which is
not `active`, so even adding an `entra_tenant_id` by hand would not produce a sign-in.

A policy of the form "visible to admins of the legacy tenant" therefore describes an unreachable
state: there is no such admin and no way to become one. Nor can an admin of a *real* tenant be let
in, because that is precisely the `Deny("cross_tenant")` of 7.4 step 2 - the one rule the whole
model rests on. Punching a hole in it for legacy data would let an admin of any tenant read data
that may have belonged to a different customer entirely, which is the failure this spec exists to
prevent.

The honest answer is the third one: **the data does not become visible by relaxing a rule, it
becomes visible by being moved.**

#### 8.5.3 The adoption path

Adoption is an operator action on the same trust boundary as tenant creation (4.4), on the same
`TenantsController`, with the same two traps closed (unconditional header; hard `503` when no
operator secret is configured):

```
POST /api/admin/tenants/{tenantId}/adopt-legacy
X-S2S-Auth: <operator secret>
Content-Type: application/json

{
  "ownerUserId": "<entra tid>:<oid>",   // optional
  "threadIds":   ["thr_1", "thr_2"],    // optional; omit to adopt all quarantined rows
  "dryRun":      true
}
```

Behaviour:

- `{tenantId}` must exist in `tenants` with `status = 'active'`; otherwise `404`, nothing written.
- Only rows whose `tenant_id` equals the quarantine tenant are eligible. A row already adopted into
  a real tenant is never re-stamped, so a repeated call is idempotent rather than destructive.
- Each adopted row gets `tenant_id = {tenantId}` and, when `ownerUserId` was supplied,
  `owner_user_id = ownerUserId`.
- **`ownerUserId` is validated before any write**: it must parse as `{tid}:{oid}`, and its `tid`
  must equal the `entra_tenant_id` of `{tenantId}`. A user id from a different Entra tenant is
  `400 owner_tenant_mismatch` - accepting it would write a row that 7.4 step 2 then denies to
  everybody, silently re-quarantining the data under a different name. A prior sign-in is
  deliberately **not** required: `oid` is stable and readable from the directory, and requiring one
  would make it impossible to hand a departed operator's conversations to their replacement.
- If `ownerUserId` is omitted, rows land in the tenant unowned. They are then visible to that
  tenant's admins - which works, because a real tenant has reachable admins - and to nobody else.
  This is the recommended first step: adopt into the tenant, let an admin look, then assign owners.
- `dryRun: true` returns the affected count and a sample without writing. Adoption is the only
  operation in P1 that moves customer data across a tenancy boundary, so it gets a rehearsal mode.
- Every call writes one `AuthorizationAuditRecord` (7.7) with `eventClass = security`, recording
  operator, target tenant, owner, and row count.

This replaces `Identity:LegacyConversationPolicy` entirely - there is no `AdminOnly`,
`AssignTo:{userId}`, or `Shared` mode. `AssignTo` was unimplementable as written: it named a user
at *migration* time, when no tenant mapping exists to validate them against, and it would have
assigned an owner from one tenant to a row stamped with another.

**Development and demo installs** need no special visibility mode. They run `Identity:Enforce=false`
(7.4 step 0), where the policy allows everything and legacy rows read exactly as they do today. The
quarantine only bites once enforcement is switched on - which is the moment someone should be making
a deliberate decision about that data anyway.

`docs/deployment/AUTH_ENFORCE.md` gains a section describing the sequence: migrate, enforce off,
adopt, enforce on.

#### 8.5.4 Rollback

The migration is **additive and reversible by ignoring the columns**: with `Identity:Enforce=false`
the new columns are written but never used as a filter, so rolling back to the previous build reads
the same database successfully. Adoption is not automatically reversible - it rewrites `tenant_id` -
which is why `dryRun` exists.

### 8.6 Workspaces and chat modes - fields, not columns

These are JSON documents (2.6), so there is no DDL:

- `samples/LmStreaming.Sample/Models/Workspace.cs` - add `TenantId`, `OwnerUserId`, `Visibility`.
- `samples/LmStreaming.Sample/Models/ChatMode.cs` - add `TenantId`, `OwnerUserId`, `Visibility`.
- `samples/LmStreaming.Sample/Persistence/FileWorkspaceStore.cs`,
  `samples/LmStreaming.Sample/Persistence/FileChatModeStore.cs` - on load, a document missing the
  new fields deserializes them as `null`. Such a document is stamped with the quarantine tenant id
  of 8.5.1 on first write, which makes it invisible under enforcement until adopted - the same
  treatment SQLite rows get, reached by the same route. `adopt-legacy` (8.5.3) therefore also
  accepts `resourceType` of `workspace` and `mode`.
- `samples/LmStreaming.Sample/Persistence/IWorkspaceStore.cs`,
  `samples/LmStreaming.Sample/Persistence/IChatModeStore.cs` - `GetAllAsync` and
  `GetAllModesAsync` take a `Principal` and filter.

`Visibility` is an enum: `Private` (default), `Shared` (named grants), `TenantPublished`
(admin-published, modes only). `IsSystemDefined` is orthogonal and unchanged - a system mode stays
readable by everyone and writable by no one.

`FileChatModeStore` is registered as a process-wide singleton (`Program.cs:597`) over one flat file.
Per-user modes make that file a contention point. It stays a singleton in #304 - the file is small
and writes are rare - but this is the point at which moving modes into SQLite becomes worth
reconsidering (OQ-3).

### 8.7 Full list of files that change

| File | Change |
|---|---|
| `src/LmCore/Identity/Principal.cs` | new - `Principal`, `PrincipalRef`, `PrincipalKind`, `PrincipalSource` |
| `src/LmCore/Identity/ITenantStore.cs` | new - tenant lookup and admin binding (8.2) |
| `src/LmCore/Identity/IResourceAccessPolicy.cs` | new - `ResourceRef`, `ResourceDescriptor`, `AccessAction`, `AccessDecision` |
| `src/LmCore/Identity/IAuditSink.cs` | new - `AuthenticationAuditRecord`, `AuthorizationAuditRecord` (7.7) |
| `src/LmCore/Models/UsageRecord.cs` | add `TenantId`, `PrincipalId`, `AppId` |
| `src/LmMultiTurn/Persistence/ThreadMetadata.cs` | add `TenantId`, `OwnerUserId`, `OwnerAppId` |
| `src/LmMultiTurn/Persistence/IConversationStore.cs` | `ListThreadsAsync` takes a `Principal` |
| `src/LmMultiTurn/Persistence/Sqlite/SqliteSchemaInitializer.cs` | migration runner (slice 1) + steps 2, 3, 4, 5 |
| `src/LmMultiTurn/Persistence/Sqlite/SqliteTenantStore.cs` | new - `tenants` / `tenant_admins` reads and writes |
| `src/LmMultiTurn/Persistence/Sqlite/SqliteConversationStore.cs` | read/write new columns; scoped list query |
| `src/LmMultiTurn/Persistence/FileConversationStore.cs`, `InMemoryConversationStore.cs` | same surface, in-memory/file filter |
| `src/LmMultiTurn/UsageAccounting/UsageLedger.cs` | carry principal onto records |
| `src/LmMultiTurn/UsageAccounting/ConversationUsageProjection.cs` | write the usage rollup table |
| `src/LmAgentInfra/Agents/MultiTurnAgentPool.cs` | `AgentEntry.OwnerPrincipal`; principal guard beside the app-id guard |
| `src/LmAgentInfra/Sandbox/PrincipalConflictException.cs` | new, mirroring `SandboxCredentialConflictException` |
| `src/LmStreaming.AspNetCore/Configuration/LmStreamingOptions.cs` | `AllowedOrigins` default off `["*"]` |
| `samples/LmStreaming.Sample/Program.cs` | `AddMicrosoftIdentityWebApi`; `UseAuthentication`/`UseAuthorization` at line ~2073 |
| `samples/LmStreaming.Sample/Identity/PrincipalFactory.cs`, `IPrincipalAccessor.cs` | new |
| `samples/LmStreaming.Sample/Controllers/ConversationsController.cs` | extend `InboundS2SAuthAttribute`; scope all 14 endpoints |
| `samples/LmStreaming.Sample/Controllers/WorkspacesController.cs` | scope all 4 endpoints |
| `samples/LmStreaming.Sample/Controllers/ChatModesController.cs` | **add `[InboundS2SAuth]`**; scope endpoints |
| `samples/LmStreaming.Sample/Controllers/EmbedTokensController.cs` | new - `POST /api/embed/tokens` |
| `samples/LmStreaming.Sample/Controllers/TenantsController.cs` | new - operator tenant provisioning (4.4) and `adopt-legacy` (8.5.3) |
| `samples/LmStreaming.Sample/Controllers/DiagnosticsController.cs` | add `GET api/diagnostics/identity` (5.4) |
| `samples/LmStreaming.Sample/Models/Workspace.cs`, `Models/ChatMode.cs` | ownership fields |
| `samples/LmStreaming.Sample/Persistence/FileWorkspaceStore.cs`, `FileChatModeStore.cs`, `IWorkspaceStore.cs`, `IChatModeStore.cs` | ownership + filtering |
| `samples/LmStreaming.Sample/ClientApp/src/` | MSAL Browser sign-in; bearer on every fetch; sharing UI |
| `docs/deployment/AUTH_ENFORCE.md` | document `Identity:Enforce`, tenant provisioning, and the migrate/adopt/enforce sequence (8.5.3) |
| `CHANGELOG.md` | the `ChatModesController` behaviour change (slice 4) |

---

## 9. Usage attribution

The ledger measures correctly. The gap is attribution and queryability (2.7).

### 9.1 Record shape

`src/LmCore/Models/UsageRecord.cs` gains three nullable fields:

```csharp
/// <summary>Tenant this usage is billed to. Null only for pre-P1 records.</summary>
public string? TenantId { get; init; }

/// <summary>Effective user, '{tid}:{oid}'. Null when no human was asserted (mode C).</summary>
public string? PrincipalId { get; init; }

/// <summary>App id that made the call. Null for the interactive path pre-P1.</summary>
public string? AppId { get; init; }
```

All three are nullable so existing serialized records deserialize unchanged and
`UsageLedger.SeedFromRecords` keeps working against a database written by an earlier build.

They are populated by `UsageLedger` from the `Principal` captured on `AgentEntry.OwnerPrincipal`
(3.4). `PrincipalId` is `Principal.EffectiveUserId`, so an agent acting on behalf of a human bills
to the human while the audit record still shows the agent as `Actor` - the distinction that matters
the first time an agent's spend is disputed.

Records are already deduped by `ProviderAttemptId`; adding principal fields does not change dedup.
A merge of two observations for one attempt must keep the **first** principal rather than the
latest, because a re-merge after a restart could otherwise re-attribute spend.

### 9.2 A real table, not a JSON blob

Usage is written today into `thread_metadata.metadata_json` under `usage.aggregate` and
`usage.records` (2.7). "Show me this tenant's spend by user this month" cannot be served by parsing
every thread's JSON.

```sql
-- migration step 5
CREATE TABLE IF NOT EXISTS usage_rollup (
    tenant_id          TEXT    NOT NULL,
    principal_id       TEXT    NOT NULL,   -- '' when there is no end user (app-only)
    app_id             TEXT    NOT NULL,   -- '' when the caller is interactive
    thread_id          TEXT    NOT NULL,
    day                INTEGER NOT NULL,   -- UTC midnight, unix ms
    model_id           TEXT    NOT NULL,
    input_tokens       INTEGER NOT NULL DEFAULT 0,
    output_tokens      INTEGER NOT NULL DEFAULT 0,
    cache_read_tokens  INTEGER NOT NULL DEFAULT 0,
    cache_write_tokens INTEGER NOT NULL DEFAULT 0,
    reasoning_tokens   INTEGER NOT NULL DEFAULT 0,
    cost_micros        INTEGER NOT NULL DEFAULT 0,
    currency           TEXT    NOT NULL DEFAULT 'USD',
    PRIMARY KEY (tenant_id, principal_id, app_id, thread_id, day, model_id)
);
CREATE INDEX IF NOT EXISTS idx_usage_rollup_tenant_day
  ON usage_rollup (tenant_id, day DESC);
```

`ConversationUsageProjection` writes here in addition to - not instead of - the existing metadata
keys, so the per-conversation usage endpoint (`GET /api/conversations/{threadId}/usage`,
`ConversationsController.cs:454`) is unaffected.

Aggregation is idempotent on the primary key, matching the ledger's existing `ProviderAttemptId`
dedup discipline: a replayed projection must `UPSERT` to the folded value, not `+=`, or a restart
would double-count.

**Two things about that key are deliberate and both are easy to get wrong.**

*`principal_id` and `app_id` are `NOT NULL` with `''` as the "not applicable" sentinel.* SQLite
permits `NULL` in a non-`INTEGER` `PRIMARY KEY` column, and for uniqueness purposes `NULL` is not
equal to `NULL`. A nullable `principal_id` would therefore mean `ON CONFLICT` never fires for
app-only traffic: every projection replay would insert another row instead of folding, and the
resulting over-count would show up as inflated spend on exactly the mode-C daemons that bill by
outcome. `''` compares equal to `''`, so the UPSERT works.

*`app_id` is part of the key.* Two apps in the same tenant driving the same thread on the same day
with the same model are separate lines of attribution; leaving `app_id` out of the key would fold
them together and simultaneously make the stored `app_id` value non-deterministic - last writer
wins. 9.1 attributes usage to `(tenant, app, principal)`, and the key has to carry all three for
the table to answer the question 9.3 asks of it.

### 9.3 The tenant-admin view

`GET /api/admin/usage?from=&to=&groupBy=user|app|model` - requires role `admin`, and filters
`tenant_id = principal.TenantId` unconditionally. Returns per-group token counters and
`cost_micros`. Cost stays in integer micros end to end; no floating point.

Each `groupBy` value maps to one key column of 9.2: `user` -> `principal_id`, `app` -> `app_id`,
`model` -> `model_id`. `groupBy=app` is the reason `app_id` is part of the primary key rather than
a payload column - grouping by a column that is not in the key would sum rows that the UPSERT had
already folded under a different, arbitrarily chosen `app_id`. Rows carrying the `''` sentinel are
reported as `(none)` rather than being dropped, so a tenant's totals across groups always reconcile
with its ungrouped total.

Relates to [#116](https://github.com/achieveai/LmDotnetTools/issues/116) - cached and reasoning
token counts are dropped on some usage paths, and `PromptTokens` semantics differ across providers.
This spec does not fix #116; it does mean an inaccuracy there now surfaces on a customer-visible
bill rather than only in a diagnostic panel, which raises its priority.

---

## 10. Broker-layer evaluation (Zitadel-style)

The question: do we integrate Entra directly, or put an identity broker (Zitadel, Keycloak, Auth0,
WorkOS) in front so that Entra becomes one upstream connector among many?

### 10.1 What a broker buys

1. **One integration for N upstream IdPs.** Okta, Google Workspace, Ping, and per-customer SAML
   become configuration rather than code. This is the real prize and it arrives the first time a
   prospect is not on Entra.
2. **Per-tenant IdP configuration as data.** Onboarding a customer's directory becomes an admin
   action, not a deployment.
3. **A native login path.** #237 identifies a case federation cannot serve: a human clicking a
   deep link hours later with no live client-service session to exchange. A broker supplies that
   without us building password storage, MFA, or account recovery.
4. **Token exchange as a product feature.** Zitadel implements RFC 8693 directly, so parts of the
   OBO path in 4.2 become configuration rather than the validation code of section 5.
5. **A user, org, and role model out of the box** - the thing section 7.3 currently keeps
   deliberately thin.

### 10.2 What it costs

1. **A new stateful component on the critical path.** Every sign-in depends on its availability and
   its database. Nothing is more visible to a customer than an IdP outage.
2. **Duplicated tenancy.** Broker orgs and our `tenant_id` are two directories that must agree.
   Divergence produces exactly the failure #237 warns about: one human arriving by two paths
   becomes two principals with different permissions, and the resulting "why can't I see my own
   conversation?" tickets are miserable to diagnose.
3. **We build the section 5 validator anyway.** Modes B and C accept a *host-minted* OBO JWT. That
   is the host's issuer, not the broker's. A broker removes none of that work.
4. **Operational surface.** Signing keys, backups, upgrades, and its own dependency updates.
5. **It does not shorten day one.** The first customer is on Entra. A broker adds a hop and a
   deployment before delivering any value.

### 10.3 Recommendation

**Go direct to Entra now. Keep the broker as a strictly additive, later option, and pay only the
one design cost that preserves it.**

That cost is small and is already in this spec: the codebase must depend on `Principal`, never on
Entra claim names. Concretely -

- `PrincipalFactory` (3.5) is the only type in the repository that knows the strings `oid`, `tid`,
  `scp`, or `roles`.
- `Identity:Apps:{appId}` (5.2) already carries a per-app issuer, audience, and JWKS URI. A broker
  is just another entry in that map.
- `Principal.Source` distinguishes doors without leaking which vendor is behind one.

With those in place, introducing a broker later means re-pointing configuration and adding one
issuer registration. It does not mean touching the stores, the pool, the policy, or the ledger.

Revisit when any of these becomes true: a second non-Entra upstream directory is required; the
native-login deep-link case from #237 is scheduled; or per-tenant IdP configuration is needed as a
self-service admin action. Until then a broker is infrastructure carrying no load.

---

## 11. Delivery slices

Ordered. Each is one PR. `Identity:Enforce=false` throughout, flipped only after #306.

### Slice 1 - [#301] Sign-in with Entra; `Principal` on every request

**Ships.** `src/LmCore/Identity/Principal.cs` and `IResourceAccessPolicy.cs`.
`Microsoft.Identity.Web` in the sample; `AddMicrosoftIdentityWebApi` plus `UseAuthentication` and
`UseAuthorization` at `Program.cs:~2073`. `IPrincipalAccessor` and `PrincipalFactory`. MSAL Browser
sign-in in ClientApp with a bearer header on every fetch. `AllowedOrigins` default changed off
`["*"]`. The global `Identity:Enforce` flag, defaulting false. `IAuditSink` over structured logs
(7.7).

Explicit provisioning (4.4) also puts the following in this slice:

- The `PRAGMA user_version` migration runner (8.1), because the tenant tables are themselves a
  migration step.
- Migration step 2 - `tenants` and `tenant_admins` (8.2). Database reaches `user_version` 2.
- `ITenantStore` / `SqliteTenantStore`; `tid` -> internal tenant id resolution on every sign-in.
- `TenantsController` with `POST /api/admin/tenants`, guarded unconditionally by the operator
  secret and failing closed (`503`) when that secret is unset - both traps named in 4.4.
- `Identity:SeedTenants`, applied idempotently at startup and only when `Enforce=false`.
- The rejection screen in ClientApp for `tenant_not_provisioned` / `tenant_suspended`.
- First-admin binding from `preferred_username` on first sign-in.

**Depends on.** Nothing.

**Verified by.** A signed-in user from a provisioned tenant resolves to
`Principal{Source=Interactive, Actor=(EndUser, "{tid}:{oid}"), TenantId="tnt_..."}`. A validly
signed token from an **unprovisioned** Entra tenant is `403` `tenant_not_provisioned`, is audited,
and does **not** create a tenant - asserted by row count before and after. A suspended tenant is
`403` `tenant_suspended`. The rejection response does not redirect to Entra, so a client cannot
enter a sign-in loop. `POST /api/admin/tenants` without the operator secret is `401`; with the
secret unconfigured it is `503`, never success. The named first admin, on first sign-in, is bound
to `{tid}:{oid}` and carries role `admin`; a second sign-in does not rebind. With `Enforce=false`
every existing integration test passes unchanged - this is the regression gate for the whole
pillar. With `Enforce=true` an anonymous `/api/conversations` call is `401`.

### Slice 2 - [#302] Conversation ownership

**Ships.** Migration steps 3 and 4 - owner columns on `thread_metadata`, the quarantine tenant row
and backfill (8.5.1), the `resource_grants` table, the owner index. Database goes from
`user_version` 2 to 4. The runner itself already exists from slice 1.
`TenantsController` gains `POST /api/admin/tenants/{tenantId}/adopt-legacy` (8.5.3), with `dryRun`.
`ThreadMetadata` fields. `ListThreadsAsync(Principal, ...)` with the SQL predicate of 7.5 across all
three store implementations. Ownership written at `POST /api/conversations`
(`ConversationsController.cs:221`), which also closes #162. `PrincipalConflictException` and the
principal guard beside the app-id guard in `MultiTurnAgentPool`. Named sharing endpoints
(`POST` and `DELETE /api/conversations/{threadId}/shares`) and the sharing UI. All 14 controller
endpoints scoped.

**Depends on.** Slice 1.

**Verified by.** Two users in one tenant each see only their own conversations. A cross-tenant
`GET /api/conversations/{id}` is `404`, not `403`. A shared conversation appears for the grantee as
`viewer` and is not writable. A tenant admin's `GET /api/conversations` and their
`GET /api/conversations/{id}` agree on the same row set - the specific defect a missing admin
branch in 7.5 would produce. A pre-existing conversation, after migration, is invisible to a
`member` **and** to an `admin` of every provisioned tenant, and becomes visible only after
`adopt-legacy`; `dryRun` writes nothing; an `ownerUserId` whose `tid` does not match the target
tenant is `400` and writes nothing; calling `adopt-legacy` twice adopts the same rows once. A
database at `user_version` 1 opened by the new build reaches **4** with no data loss, and the
resulting database still opens under the previous build.

### Slice 3 - [#303] Workspace ownership

**Ships.** `TenantId`, `OwnerUserId`, `Visibility` on `Workspace`; `FileWorkspaceStore`
legacy-tolerant load and rewrite; `IWorkspaceStore.GetAllAsync(Principal, ...)`; all four
`WorkspacesController` endpoints scoped; grants reuse `resource_grants` with
`resource_type='workspace'`.

**Depends on.** Slice 2, for `resource_grants` and `IResourceAccessPolicy`.

**Verified by.** `GET /api/workspaces` returns only owned plus granted plus system-defined. A
workspace `use` without a grant is refused even when `read` is granted. A `workspaces.json` written
by the previous build loads without the new fields and is rewritten with them on first save.

### Slice 4 - [#304] Per-user chat modes

**Ships.** `[InboundS2SAuth]` **added to `ChatModesController`** - an existing hole (2.3).
Ownership fields on `ChatMode`; `FileChatModeStore` legacy-tolerant load; `IChatModeStore`
filtering; `Visibility.TenantPublished` and an admin-only publish endpoint; system modes remain
read-only for all.

**Depends on.** Slice 2.

**Verified by.** A mode created by user A is invisible to user B. An admin-published mode is
visible to every member of the tenant and editable only by an admin. A non-admin `publish` is
`403`. A request to `/api/chat-modes` carrying `X-Sbx-App-Id` without `X-S2S-Auth` is now `401`
where it previously succeeded. This is a deliberate breaking change to an existing endpoint's
behaviour and closes the hole named in 2.3; it needs a `CHANGELOG.md` entry and a note in
`docs/deployment/AUTH_ENFORCE.md`, because an existing integrator sending only `X-Sbx-App-Id` will
start failing on upgrade.

### Slice 5 - [#305] Host-asserted OBO on the S2S path

**Ships.** The section 5 validator: per-app issuer/audience/JWKS registration under
`Identity:Apps`, a `kid`-keyed key cache with the 24h/1h/5min policy, 120s clock skew, the `jti`
replay set, `act`-chain mapping, and the tenant-agreement check (5.1 step 11).
`InboundS2SAuthAttribute` extended per 4.2, including the no-fallback rule on an invalid OBO token.
`GET /api/diagnostics/identity` on the existing `DiagnosticsController` (5.4), reporting `enforce`,
`replayStore` and `replicaSafe`, plus the startup Warning when the replay store is in-memory.

**Depends on.** Slice 1, for `Principal` and for the `tenants` table that step 7 of 5.1 resolves
against. Still independent of slices 2-4, so it can run in parallel with them - explicit
provisioning moved the tenant registry *earlier*, into slice 1, so it did not add a dependency here.

**Verified by.** A request with a valid app credential and a valid OBO JWT yields
`Principal{Actor=(App,...), OnBehalfOf=(EndUser,...), Source=HostAsserted}`. An app holding a scope
while acting for a user without it is **refused** - the intersection rule (3.2). An invalid OBO JWT
is `401` and does **not** fall back to `AppOnly`. A replayed `jti` is `401` and produces a
security-classified audit record. A token whose `tid` resolves to a tenant other than the app's
onboarded internal tenant id is `403 tenant_mismatch`; a token whose `tid` resolves to no row is
`403 tenant_not_provisioned`; an expired or badly signed token is `401` - the 401/403 split of 5.1
is asserted per case, not just "rejected". A host whose `Identity:Apps:*:TenantId` names a tenant
absent from the `tenants` table fails **startup**, not a request.
`GET /api/diagnostics/identity` reports `replicaSafe: false` while the replay store is in-memory.
A request with no `Authorization` header behaves byte-identically to today.

### Slice 6 - [#306] Usage attribution and the tenant-admin view

**Ships.** `TenantId`, `PrincipalId`, `AppId` on `UsageRecord`; population from
`AgentEntry.OwnerPrincipal`; migration step 5 creating `usage_rollup`; the `UPSERT` projection;
`GET /api/admin/usage`; the admin UI panel.

**Depends on.** Slices 1, 2, and 5 - a mode-B principal must exist before spend can be attributed
to it.

**Verified by.** Spend on a conversation created by user A rolls up to A, filtered to A's tenant.
An agent acting on behalf of A bills to A while the audit shows the agent as actor. Replaying the
projection twice does not double-count. A `member` calling `/api/admin/usage` is `403`. Records
written before this slice deserialize with null attribution fields and are reported under an
explicit "unattributed" group - not silently dropped, and not folded into an arbitrary user.

### Slice 7 - [#309] Embed token and the packaged iframe

**Ships.** `POST /api/embed/tokens` (`EmbedTokensController`), RFC 9068 minting with the section
6.2 claims and the 15-minute derived-lifetime ceiling; the `lmstreaming-embed` audience and its
validation filter with the `res` single-resource check; the `/embed` route with per-app
`frame-ancestors` and `X-Frame-Options: DENY` elsewhere; the `postMessage` handshake on both sides
with explicit `targetOrigin`; a host-side embed snippet in the docs.

**Depends on.** Slices 1, 2, and 5.

**Verified by.** A host backend with a valid app credential and OBO JWT mints a token scoped to one
conversation. That token cannot list conversations, cannot read a second conversation, and cannot
reach `/api/admin/*`. Its lifetime never exceeds the remaining lifetime of the authorising OBO JWT.
The token never appears in a URL - asserted by a test scanning navigation history and request URLs.
Framing from an unregistered origin is blocked by `frame-ancestors`. An embed token presented on
the general API audience is rejected.

### Slice ordering summary

```
#301 --+-- #302 --+-- #303
       |          +-- #304
       |          |
       +-- #305 --+-- #306
                  +-- #309   (also needs #302)
```

---

## 12. Open questions

Decisions that were once open and are now recorded in the body, listed here only so a reader looking
for them does not conclude they were dropped: `Identity:Enforce` is global (4.1), tenants are
explicitly provisioned (4.4), the `jti` replay store is in-memory and single-replica for now (5.4),
and audit goes to structured logs until P4 exists (7.7). The questions below are genuinely
undecided.

**OQ-1 - What happens to a conversation when its owner leaves the tenant?**
Options: transfer to a tenant admin, soft-delete, or leave it orphaned and admin-visible. This
affects whether `owner_user_id` needs a foreign key to a users table. Note that provisioning has
partly changed the ground here: `tenant_admins` (8.2) now stores a per-user row, but only for
admins, so there is still no general user record for an ordinary member - only the opaque
`{tid}:{oid}` string on the rows they own.

**OQ-2 - Offset pagination over a mutable sort key.**
`ListThreadsAsync(limit, offset, ct)` (`src/LmMultiTurn/Persistence/IConversationStore.cs:115`)
orders by `last_updated DESC`, which changes while a user pages, so rows are skipped. Pre-existing,
not caused by P1. Fixing it means a keyset cursor and an interface change; doing it inside slice 2 -
which already changes that signature - is cheaper than doing it later, but it widens the slice.

**OQ-3 - Do modes and workspaces move into SQLite?**
Both are single flat JSON files behind process-wide singletons (2.6). Per-user data multiplies the
entry count and makes the whole-file rewrite a contention point. Slices 3 and 4 keep the file
stores. The threshold at which that stops being acceptable has not been measured.

**OQ-4 - What tenant does a mode-C daemon operate in?**
`CodeReviewDaemon.Sample` authenticates with an app credential and asserts no human. Its `Principal`
is `Source=AppOnly` with `EffectiveUserId == null`, so its spend attributes to a tenant and an app
but to no user. Whether that appears in the admin view as a first-class "service" row, or is
excluded from per-user reporting, is a product decision that section 9.3 does not settle. Explicit
provisioning sharpens this: a daemon's app registration must name a provisioned tenant before it can
run at all, so someone has to decide whether internal daemons get their own tenant row.

**OQ-5 - Are agent principals minted per run?**
#237 specifies `PrincipalKind.Agent` with `OnBehalfOf` set to the initiating human, so audit can
distinguish "the agent did X for Alice" from "Alice did X". This spec defines the enum value but no
slice mints one - slices 1-7 only ever produce `EndUser`, `App`, or `Service` actors. Whether
sub-agent and workflow runs get their own `Agent` principal, and whether that principal is
persisted, is deferred and should be settled before P2 builds on it.
