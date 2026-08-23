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
appear on next open. Adding a *column* to an existing table has no path today. Slice #302 must
build one before it can add a column.

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

`TenantId` is the Entra `tid` claim for interactive users. For host-asserted principals it is the
tenant the app registration is onboarded to, not a value the caller supplies (section 5.1).

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
    "AllowedTenants": [ "<tenant-guid>" ],
    "Enforce": false
  }
}
```

`Microsoft.Identity.Web` is chosen over raw `JwtBearer` because it implements multi-tenant issuer
validation correctly out of the box (5.1 step 3) and because MSAL (`Microsoft.Identity.Client`
4.84.1) is already a dependency of both `src/LmAgentInfra` and the sample.

**`TenantId: organizations`** admits any work or school tenant but not personal Microsoft accounts.
Issuer validation alone only proves *some* real Entra tenant signed the token - it does not prove
the tenant is a customer. The `tid` claim is therefore checked against `Identity:AllowedTenants` as
a separate authorization step, and an unknown `tid` is rejected with `403` and an audit record.

**Pipeline placement.** `UseAuthentication()` then `UseAuthorization()` between `Program.cs:2072`
and `:2075`. `UseCors` inside `UseLmStreaming()` already precedes `MapControllers`; the
`AllowedOrigins` default must change from `["*"]` to a configured list in the same slice.

**Rollout.** `Identity:Enforce=false` (default) keeps every current call path working, resolving
unauthenticated interactive requests to the legacy principal of section 8.4. `Identity:Enforce=true`
requires a validated token on every `/api/*` route. This mirrors the `AUTH_ENFORCE` deploy
discipline in `docs/deployment/AUTH_ENFORCE.md`: deploy with enforcement off, onboard callers, then
flip.

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

---

## 5. Token validation rules for the OBO JWT

Fail closed at every step. Any failure is `401`, an audit record, and no principal.

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
7. **Tenant.** `tid` must be present and must be in `Identity:AllowedTenants`. A well-signed token
   from a tenant we do not serve is `403`, not `401` - it authenticated fine, it is just not a
   customer.
8. **Subject.** `oid` must be present; the user id is `{tid}:{oid}` (3.3).
9. **Replay.** `jti` must be present and unseen (5.4).
10. **Actor chain.** If `act` is present, record it into `DelegationChain` (5.5).
11. **Tenant agreement.** The `tid` in the token must equal the tenant the presenting app id is
    onboarded to. A mismatch means an app registered for tenant A is asserting a user of tenant B -
    reject with `403` and raise it as a security event, not a routine denial.

Step 11 is the cross-tenant containment check. Without it, a single compromised app credential
could assert users in every tenant.

### 5.2 Key distribution and rotation

**Preferred: per-app JWKS URI.** Each onboarded app registration records:

```json
"Identity": {
  "Apps": {
    "acme-portal": {
      "TenantId": "<tenant-guid>",
      "Issuer": "https://login.microsoftonline.com/{tenantid}/v2.0",
      "Audience": "<api client id>",
      "JwksUri": "https://login.microsoftonline.com/<tenant>/discovery/v2.0/keys"
    }
  }
}
```

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
on second presentation. A replayed `jti` is `401` **and** an audit record classified as a security
event, not a routine denial - a replay is evidence, and a log line that looks like an ordinary
`401` will not be noticed.

**Store.** Single-instance today: an in-memory expiring set. This is honestly insufficient the
moment the API runs more than one replica, because a replay directed at a second instance would
succeed. The durable, shared implementation depends on the coordination core (lease, fence, CAS
store) delivered by [#236](https://github.com/achieveai/LmDotnetTools/issues/236). Until #236 lands,
a multi-instance deployment must either use sticky routing or accept the gap explicitly. Recorded
as OQ-4.

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
| `client_id` | Required by RFC 9068; the app id |

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
- The host is advised to set `sandbox="allow-scripts allow-forms allow-popups"`. Note that
  `allow-scripts` together with `allow-same-origin` on same-origin content lets the framed document
  remove its own `sandbox` attribute, nullifying the sandbox - so that pairing is only safe
  cross-origin, which the embed is.
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
4. **Absent means denied.** A resource with no owner is not public; see the migration policy in 8.4.
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
public readonly record struct ResourceRef(string Type, string Id);

public enum AccessAction { Read, Use, Write, Delete, Share, Publish }

public sealed record AccessDecision(bool Allowed, string Reason)
{
    public static AccessDecision Deny(string reason) => new(false, reason);
    public static readonly AccessDecision AllowOwner  = new(true, "owner");
    public static readonly AccessDecision AllowGrant  = new(true, "grant");
    public static readonly AccessDecision AllowAdmin  = new(true, "tenant_admin");
    public static readonly AccessDecision AllowSystem = new(true, "system_defined");
}

public interface IResourceAccessPolicy
{
    ValueTask<AccessDecision> EvaluateAsync(
        Principal principal,
        ResourceRef resource,
        AccessAction action,
        CancellationToken ct = default);
}
```

Named `IResourceAccessPolicy`, not `IAuthorizationService`, to avoid colliding with the ASP.NET
Core interface of that name.

Evaluation order, first match wins:

1. `resource.TenantId != principal.TenantId` -> `Deny("cross_tenant")`.
2. Resource is system-defined (`IsSystemDefined`) and action is `read`/`use` -> `AllowSystem`.
   System-defined resources are readable by every member of every tenant and writable by no one.
3. `resource.OwnerUserId == principal.EffectiveUserId` -> `AllowOwner`.
4. A grant exists for `(tenant, resource, principal.EffectiveUserId)` conferring the action ->
   `AllowGrant`.
5. `principal.Roles` contains `admin` and the action is permitted for admins -> `AllowAdmin`,
   audited.
6. Otherwise -> `Deny("no_grant")`.

`AccessDecision.Reason` is written to the audit record for allows as well as denies. A deny-only
audit cannot answer "was this ever attempted successfully?".

### 7.5 Listing is a filter, not a loop

Listing endpoints must not fetch-then-filter. `IConversationStore.ListThreadsAsync` gains a
principal parameter and pushes the predicate into SQL:

```sql
SELECT ... FROM thread_metadata t
WHERE t.tenant_id = @tenantId
  AND ( t.owner_user_id = @userId
        OR EXISTS (SELECT 1 FROM resource_grants g
                   WHERE g.tenant_id = @tenantId
                     AND g.resource_type = 'conversation'
                     AND g.resource_id = t.thread_id
                     AND g.subject_id = @userId
                     AND (g.expires_at IS NULL OR g.expires_at > @now)) )
ORDER BY t.last_updated DESC
LIMIT @limit OFFSET @offset;
```

In-memory filtering after a `LIMIT` would silently return short pages - the page would be trimmed
after the database had already truncated it.

Separately, `ListThreadsAsync(limit, offset, ct)`
(`src/LmMultiTurn/Persistence/IConversationStore.cs:115`) paginates by offset over
`last_updated DESC`, a **mutable** sort key: a conversation touched between page 1 and page 2 moves
and a row is skipped. Adding the owner filter does not cause this, but it does make it more
visible. Recorded as OQ-5.

### 7.6 The pool guard gains a principal dimension

`MultiTurnAgentPool`'s app-id freeze is **kept exactly as it is** - it is the tenancy boundary and
removing it would change gateway ownership semantics. A second, parallel check is added on
`OwnerPrincipal.EffectiveUserId`, throwing a new `PrincipalConflictException` on mismatch, mapped
to `409` with `code = "principal_conflict"` alongside the existing `caller_credential_conflict`
(`ConversationsController.cs:759`).

This is what makes the guard mean something in the UI, where today both sides are `null` and
therefore always match (2.2).

---

## 8. Data model changes

### 8.1 A migration mechanism must exist first

There is none (2.5). Before any column can be added, `SqliteSchemaInitializer` needs a versioned
migration runner. Recommendation: `PRAGMA user_version`, an ordered array of migration steps, each
applied in a single transaction with `user_version` bumped in that same transaction.

The existing `CREATE TABLE IF NOT EXISTS` block becomes migration step 1, so a database created by
an earlier build is recognised as already at version 1 without re-running DDL.

File changed: `src/LmMultiTurn/Persistence/Sqlite/SqliteSchemaInitializer.cs`.

This is genuinely part of slice #302 and is the reason that slice is larger than it looks.

### 8.2 New columns on `thread_metadata`

```sql
-- migration step 2
ALTER TABLE thread_metadata ADD COLUMN tenant_id     TEXT;
ALTER TABLE thread_metadata ADD COLUMN owner_user_id TEXT;
ALTER TABLE thread_metadata ADD COLUMN owner_app_id  TEXT;
CREATE INDEX IF NOT EXISTS idx_thread_metadata_owner
  ON thread_metadata (tenant_id, owner_user_id, last_updated DESC);
```

Nullable, because SQLite cannot add a `NOT NULL` column without a default and because null is the
signal for "legacy, unclaimed" (8.4). The index exactly matches the `WHERE` and `ORDER BY` of 7.5.

`owner_app_id` records which app created the conversation - it is the durable form of the
`CallerCredential` freeze, which today exists only in the in-memory pool and is lost on restart.
This also closes [#162](https://github.com/achieveai/LmDotnetTools/issues/162) (bind S2S ownership
at `Provision` rather than at first `SendMessage`), because `POST /api/conversations`
(`ConversationsController.cs:221`) can now write ownership at provision time.

Corresponding fields are added to `ThreadMetadata`
(`src/LmMultiTurn/Persistence/ThreadMetadata.cs`) as first-class properties - **not** into
`Properties`, which is serialized into `metadata_json` and cannot be filtered in SQL.

### 8.3 New table: `resource_grants`

```sql
-- migration step 3
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

### 8.4 Migration of existing rows

The hard question: what tenant and owner do conversations that already exist get?

**They get a tenant and no owner.** Migration step 2 backfills:

```sql
UPDATE thread_metadata SET tenant_id = @legacyTenantId WHERE tenant_id IS NULL;
```

where `@legacyTenantId` comes from `Identity:LegacyTenantId`, defaulting to the literal `"legacy"`.
`owner_user_id` stays `NULL`, meaning **unclaimed**.

Visibility of unclaimed rows is governed by `Identity:LegacyConversationPolicy`:

| Value | Behaviour | Intended for |
|---|---|---|
| `AdminOnly` (**default**) | Visible only to principals holding `admin` | Production |
| `AssignTo:{userId}` | Backfilled to that user at migration time | Single-operator installs |
| `Shared` | Visible to every member of the legacy tenant | Development and demo |

`AdminOnly` is the fail-closed default: no pre-existing conversation becomes visible to a user who
could not already see it. `Shared` is explicitly a development convenience and must be documented
as such in `docs/deployment/AUTH_ENFORCE.md`.

The migration is **additive and reversible by ignoring the columns**: with `Identity:Enforce=false`
the new columns are written but never used as a filter, so rolling back to the previous build reads
the same database successfully.

### 8.5 Workspaces and chat modes - fields, not columns

These are JSON documents (2.6), so there is no DDL:

- `samples/LmStreaming.Sample/Models/Workspace.cs` - add `TenantId`, `OwnerUserId`, `Visibility`.
- `samples/LmStreaming.Sample/Models/ChatMode.cs` - add `TenantId`, `OwnerUserId`, `Visibility`.
- `samples/LmStreaming.Sample/Persistence/FileWorkspaceStore.cs`,
  `samples/LmStreaming.Sample/Persistence/FileChatModeStore.cs` - on load, a document missing the
  new fields deserializes them as `null` and is treated as legacy under the same
  `LegacyConversationPolicy`; the file is rewritten with the fields present on first write.
- `samples/LmStreaming.Sample/Persistence/IWorkspaceStore.cs`,
  `samples/LmStreaming.Sample/Persistence/IChatModeStore.cs` - `GetAllAsync` and
  `GetAllModesAsync` take a `Principal` and filter.

`Visibility` is an enum: `Private` (default), `Shared` (named grants), `TenantPublished`
(admin-published, modes only). `IsSystemDefined` is orthogonal and unchanged - a system mode stays
readable by everyone and writable by no one.

`FileChatModeStore` is registered as a process-wide singleton (`Program.cs:597`) over one flat file.
Per-user modes make that file a contention point. It stays a singleton in #304 - the file is small
and writes are rare - but this is the point at which moving modes into SQLite becomes worth
reconsidering (OQ-6).

### 8.6 Full list of files that change

| File | Change |
|---|---|
| `src/LmCore/Identity/Principal.cs` | new - `Principal`, `PrincipalRef`, `PrincipalKind`, `PrincipalSource` |
| `src/LmCore/Identity/IResourceAccessPolicy.cs` | new - `ResourceRef`, `AccessAction`, `AccessDecision` |
| `src/LmCore/Models/UsageRecord.cs` | add `TenantId`, `PrincipalId`, `AppId` |
| `src/LmMultiTurn/Persistence/ThreadMetadata.cs` | add `TenantId`, `OwnerUserId`, `OwnerAppId` |
| `src/LmMultiTurn/Persistence/IConversationStore.cs` | `ListThreadsAsync` takes a `Principal` |
| `src/LmMultiTurn/Persistence/Sqlite/SqliteSchemaInitializer.cs` | migration runner + steps 2, 3, 4 |
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
| `samples/LmStreaming.Sample/Models/Workspace.cs`, `Models/ChatMode.cs` | ownership fields |
| `samples/LmStreaming.Sample/Persistence/FileWorkspaceStore.cs`, `FileChatModeStore.cs`, `IWorkspaceStore.cs`, `IChatModeStore.cs` | ownership + filtering |
| `samples/LmStreaming.Sample/ClientApp/src/` | MSAL Browser sign-in; bearer on every fetch; sharing UI |
| `docs/deployment/AUTH_ENFORCE.md` | document `Identity:Enforce` beside `AUTH_ENFORCE` |

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
-- migration step 4
CREATE TABLE IF NOT EXISTS usage_rollup (
    tenant_id          TEXT    NOT NULL,
    principal_id       TEXT,
    app_id             TEXT,
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
    PRIMARY KEY (tenant_id, principal_id, thread_id, day, model_id)
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

### 9.3 The tenant-admin view

`GET /api/admin/usage?from=&to=&groupBy=user|app|model` - requires role `admin`, and filters
`tenant_id = principal.TenantId` unconditionally. Returns per-group token counters and
`cost_micros`. Cost stays in integer micros end to end; no floating point.

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
`["*"]`. The `Identity:Enforce` flag, defaulting false. **No store or schema change.**

**Depends on.** Nothing.

**Verified by.** A signed-in request resolves to
`Principal{Source=Interactive, Actor=(EndUser, "{tid}:{oid}")}`. A token from a `tid` outside
`Identity:AllowedTenants` is `403`. With `Enforce=false` every existing integration test passes
unchanged - this is the regression gate for the whole pillar. With `Enforce=true` an anonymous
`/api/conversations` call is `401`.

### Slice 2 - [#302] Conversation ownership

**Ships.** The `PRAGMA user_version` migration runner (8.1) - the bulk of this slice. Migration
steps 2 and 3: owner columns on `thread_metadata`, the `resource_grants` table, the owner index.
`ThreadMetadata` fields. `ListThreadsAsync(Principal, ...)` with the SQL predicate of 7.5 across all
three store implementations. Ownership written at `POST /api/conversations`
(`ConversationsController.cs:221`), which also closes #162. `PrincipalConflictException` and the
principal guard beside the app-id guard in `MultiTurnAgentPool`. Named sharing endpoints
(`POST` and `DELETE /api/conversations/{threadId}/shares`) and the sharing UI. All 14 controller
endpoints scoped.

**Depends on.** Slice 1.

**Verified by.** Two users in one tenant each see only their own conversations. A cross-tenant
`GET /api/conversations/{id}` is `404`, not `403`. A shared conversation appears for the grantee as
`viewer` and is not writable. After migration with `LegacyConversationPolicy=AdminOnly`, a
pre-existing conversation is invisible to a `member` and visible to an `admin`. A database at
version 1 opened by the new build reaches version 3 with no data loss, and the resulting database
still opens under the previous build.

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
where it previously succeeded - an intentional behaviour change that must be called out in the PR
body.

### Slice 5 - [#305] Host-asserted OBO on the S2S path

**Ships.** The section 5 validator: per-app issuer/audience/JWKS registration under
`Identity:Apps`, a `kid`-keyed key cache with the 24h/1h/5min policy, 120s clock skew, the `jti`
replay set, `act`-chain mapping, and the tenant-agreement check (5.1 step 11).
`InboundS2SAuthAttribute` extended per 4.2, including the no-fallback rule on an invalid OBO token.

**Depends on.** Slice 1, for `Principal`. Independent of slices 2-4, so it can run in parallel with
them.

**Verified by.** A request with a valid app credential and a valid OBO JWT yields
`Principal{Actor=(App,...), OnBehalfOf=(EndUser,...), Source=HostAsserted}`. An app holding a scope
while acting for a user without it is **refused** - the intersection rule (3.2). An invalid OBO JWT
is `401` and does **not** fall back to `AppOnly`. A replayed `jti` is `401` and produces a
security-classified audit record. A token whose `tid` differs from the app's onboarded tenant is
`403`. A request with no `Authorization` header behaves byte-identically to today.

### Slice 6 - [#306] Usage attribution and the tenant-admin view

**Ships.** `TenantId`, `PrincipalId`, `AppId` on `UsageRecord`; population from
`AgentEntry.OwnerPrincipal`; migration step 4 creating `usage_rollup`; the `UPSERT` projection;
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

**OQ-1 - How is a tenant created, and who is its first admin?**
This spec assumes `Identity:AllowedTenants` is operator-maintained configuration. That does not
scale past a handful of customers and has no self-service story. Whether tenant onboarding is an
admin API, a provisioning script, or driven by an Entra admin-consent callback is undecided, and it
determines whether slice 1 needs a `tenants` table.

**OQ-2 - Is `Identity:Enforce` global or per tenant?**
#237 specifies per-tenant enforcement. This spec specifies a single process-wide flag, which is
simpler but cannot stage a rollout across customers on shared infrastructure. Per-tenant
enforcement needs the tenant record of OQ-1, so the two are decided together.

**OQ-3 - What happens to a conversation when its owner leaves the tenant?**
Options: transfer to a tenant admin, soft-delete, or leave it orphaned and admin-visible. This
affects whether `owner_user_id` needs a foreign key to a users table, and whether a `users` table
is needed at all - this spec stores only the opaque `{tid}:{oid}` string with no user record.

**OQ-4 - Where does the `jti` replay set live in a multi-instance deployment?**
Section 5.4 specifies in-memory, which is correct for one instance and silently wrong for several -
a replay directed at a second replica would succeed. The durable shared store arrives with the
coordination core in #236. Until then: sticky routing, or an accepted and documented gap. Someone
must choose.

**OQ-5 - Offset pagination over a mutable sort key.**
`ListThreadsAsync(limit, offset, ct)` (`src/LmMultiTurn/Persistence/IConversationStore.cs:115`)
orders by `last_updated DESC`, which changes while a user pages, so rows are skipped. Pre-existing,
not caused by P1. Fixing it means a keyset cursor and an interface change; doing it inside slice 2 -
which already changes that signature - is cheaper than doing it later, but it widens the slice.

**OQ-6 - Do modes and workspaces move into SQLite?**
Both are single flat JSON files behind process-wide singletons (2.6). Per-user data multiplies the
entry count and makes the whole-file rewrite a contention point. Slices 3 and 4 keep the file
stores. The threshold at which that stops being acceptable has not been measured.

**OQ-7 - What tenant does a mode-C daemon operate in?**
`CodeReviewDaemon.Sample` authenticates with an app credential and asserts no human. Its `Principal`
is `Source=AppOnly` with `EffectiveUserId == null`, so its spend attributes to a tenant and an app
but to no user. Whether that appears in the admin view as a first-class "service" row, or is
excluded from per-user reporting, is a product decision that section 9.3 does not settle.

**OQ-8 - Are agent principals minted per run?**
#237 specifies `PrincipalKind.Agent` with `OnBehalfOf` set to the initiating human, so audit can
distinguish "the agent did X for Alice" from "Alice did X". This spec defines the enum value but no
slice mints one - slices 1-7 only ever produce `EndUser`, `App`, or `Service` actors. Whether
sub-agent and workflow runs get their own `Agent` principal, and whether that principal is
persisted, is deferred and should be settled before P2 builds on it.

**OQ-9 - Does the audit trail get its own store in P1?**
Sections 5.4, 7.4, and 7.3 all require audit records, and #237 routes them through P4's outbox.
P4 does not exist yet. Whether P1 writes audit to the existing structured logs (queryable via the
DuckDB recipe in `CLAUDE.md`) as an interim, or introduces a durable table now, is unresolved. The
interim choice is cheap; the risk is that a log-only audit is not retained long enough to answer an
incident question months later.
