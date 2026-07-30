# Gateway-Keyed Workspace Catalog Design

**Date:** 2026-07-29
**Status:** Implemented
**Scope:** LmStreaming.Sample workspace persistence, marketplace compatibility, server API, and client workspace state

## Objective

Prevent workspace names, remote directory leaves, marketplace/plugin selections, and related metadata from leaking across sandbox gateways or authenticated app identities. Each LmStreaming.Sample gateway URL + AppId pair gets an independent local catalog. The server validates that catalog against the active gateway, and the client never retains a misleading list when the workspace API is unavailable.

This feature is independent of the plural agent-control work and must be implemented in a separate worktree/diff.

## Confirmed Defect

LmStreaming.Sample currently registers one flat `FileWorkspaceStore` rooted at:

```text
<AppContext.BaseDirectory>/workspaces/workspaces.json
```

The store does not include `SandboxGateway:BaseUrl` or `SandboxGateway:AppId` in its identity. Switching from a local gateway to `http://192.168.11.139:3000` reused workspace records created for the old gateway. Those records carried `gb-plugins`, while the remote gateway advertised only `claude-plugins` and `superpowers`; the remote gateway correctly returned HTTP 400 for the unknown alias.

The operational alias migration already applied to the running publish corrected the immediate request payload and live-verified remote session creation, but did not fix catalog isolation.

The client compounds the issue: `useWorkspaces.loadWorkspaces()` leaves the previous in-memory array intact when `/api/workspaces` fails, so an open page can keep showing stale records while the backend is stopped or returning 502.

## Gateway Catalog Identity

### Identity fields

The catalog identity comprises:

- Canonical sandbox gateway BaseUrl.
- Process-wide `SandboxGatewayOptions.AppId` configured for this LmStreaming.Sample instance (not per-request `SandboxCredential.AppId`).

The AppKey is never read, stored, logged, hashed, or otherwise used in catalog identity.

### Canonical URL

Accept only absolute HTTP(S) URIs. Canonicalization uses `System.Uri`, not regular expressions:

- Lowercase scheme and host.
- Reject user-info.
- Remove the fragment.
- Normalize default ports: omit `:80` for HTTP and `:443` for HTTPS.
- Preserve non-default ports.
- Strip all trailing `/` characters from the path; root `/` therefore becomes an empty path. Do not otherwise change path case, internal/doubled slashes, or percent-encoding.
- Preserve query text in `System.Uri` serialized order; query-parameter reordering intentionally produces a different identity.
- Reject malformed/relative values rather than falling back to a shared catalog.
- Do not resolve DNS aliases: `localhost` and `127.0.0.1` intentionally produce different identities.

Examples:

```text
HTTP://Example.COM:80/       -> http://example.com
https://Example.COM:443/    -> https://example.com
http://example.com:3000/    -> http://example.com:3000
https://example.com/base/   -> https://example.com/base
```

### Versioned derivation

Hash material is UTF-8:

```text
gateway-workspace-catalog:v1\0<canonical-base-url>\0<AppId>
```

Directory key:

```text
lowercase-hex(SHA-256(hash material))
```

The hash is an opaque filesystem identifier, not authentication or encryption.

## Storage Layout

```text
<AppContext.BaseDirectory>/workspaces/
  gateways/
    <gateway-key>/
      gateway.json
      workspaces.json
  legacy/
    workspaces.<UTC timestamp>.json
    migration.json
```

`gateway.json`:

```json
{
  "schemaVersion": 1,
  "derivationVersion": 1,
  "canonicalBaseUrl": "http://192.168.11.139:3000",
  "appId": "lmstreaming-sample"
}
```

The manifest is validated every time the scoped store is opened. A key whose manifest does not match the derived canonical URL/AppId is a hard startup/configuration error; the implementation must not overwrite or merge it.

Each scoped `workspaces.json` keeps the current `Workspace` record schema and atomic temp-write/rename behavior. The catalog root remains an injectable constructor/service input so unit tests and `BrowserWebAppFactory` can isolate stores under temporary directories; production alone derives it from `AppContext.BaseDirectory`. The seeded system-defined `Default` workspace remains in memory and maps to the configured default leaf; it is not persisted as a user record.

`derivationVersion: 1` is immutable for this implementation. A future derivation change must ship an explicit directory migration/redirect scheme; changing the hash-material version without migration is forbidden because it would orphan existing catalogs.

## Legacy Migration

The flat catalog has no trustworthy gateway origin. It must not be assigned automatically to whichever gateway happens to be configured during upgrade.

Migration is serialized across processes, atomic, idempotent, and retry-safe. A `legacy/migration.lock` file is opened with `FileShare.None` and held for the entire inspect/move/validate/marker transaction; another process retries with bounded backoff and then fails startup clearly rather than racing.

1. Acquire the cross-process migration lock.
2. Derive and validate the active scoped directory/manifest.
3. If the scoped catalog already exists, use it; never import the flat file into it.
4. If root-level `workspaces.json` exists and no completed migration marker exists:
   - create `legacy/`;
   - atomically write `legacy/migration.pending.json` containing schema version, initiating canonical URL/AppId, intended unique archive filename, and start timestamp;
   - atomically move the flat file to that archive filename;
   - reopen and validate the archive as the existing workspace-array JSON shape;
   - atomically replace the pending marker with `legacy/migration.json`, adding completion timestamp;
   - create/use an empty user catalog for the active gateway.
5. If interrupted, the next process reads the pending marker. It may finish that move/validation/marker regardless of its currently configured gateway, but it must preserve the **initiating** identity from the pending marker and never relabel the archive to the new gateway. A missing/mismatched archive fails safely for operator recovery.
6. Never overwrite an archive or an existing scoped catalog.
7. Legacy records are not selectable. Future explicit import tooling is out of scope.

The preexisting operational backup remains untouched.

## Remote Marketplace Compatibility

Add `WorkspaceCatalogCompatibilityService`, backed by the existing `IMarketplaceCatalogClient`.

For each workspace:

- Empty marketplace selection means gateway defaults and is `compatible` when the gateway catalog is available.
- Every explicit alias present remotely means `compatible`.
- Any missing alias means `incompatible`; `unsupportedMarketplaces` preserves the workspace's stored alias order after ordinal de-duplication, so output is deterministic and actionable.
- Gateway preview unavailable means `unknown`.

Compatibility checks never mutate stored workspace records.

Remote preview may be cached in memory for 30 seconds per process. Cache behavior must:

- be cancellation-safe;
- single-flight concurrent refreshes;
- never persist remote catalog responses;
- expire failures no later than successful results;
- expose unavailable/unknown rather than reuse a catalog from another gateway identity.

## Server API

### List

`GET /api/workspaces` returns:

```json
{
  "gateway": {
    "canonicalBaseUrl": "http://192.168.11.139:3000",
    "appId": "lmstreaming-sample",
    "available": true,
    "error": null
  },
  "workspaces": [
    {
      "id": "...",
      "name": "LmDotNettools",
      "directoryRelPath": "lmdotnettools",
      "marketplaces": ["claude-plugins", "superpowers"],
      "isSystemDefined": false,
      "createdAt": 0,
      "updatedAt": 0,
      "compatibility": "compatible",
      "unsupportedMarketplaces": []
    }
  ]
}
```

Compatibility values are `compatible`, `incompatible`, or `unknown`.

### Get

`GET /api/workspaces/{id}` returns one enriched workspace using the same compatibility fields. A missing workspace remains 404.

### Create/update

Before persistence, validate requested explicit aliases against the live gateway catalog:

- Unsupported aliases: HTTP 400 with backward-compatible `error` message plus `code: "unsupported_marketplaces"`, `unsupportedMarketplaces`, and `availableMarketplaces`.
- Gateway preview unavailable: HTTP 503 with backward-compatible `error` message plus `code: "gateway_catalog_unavailable"`; do not modify persistence.

`workspacesApi.ts` also parses `code`, `unsupportedMarketplaces`, and `availableMarketplaces` into typed actionable client errors while retaining fallback support for the existing `{ error }` contract.
- Empty aliases remain valid and mean gateway defaults.

No silent alias removal, alias renaming, or fallback is allowed.

### Session creation/recreation

The compatibility gate lives in the **LmStreaming.Sample agent-creation factory** in `Program.cs`, immediately after resolving the selected `Workspace` and before calling `SandboxSessionRegistry.GetOrCreateLiveSessionAsync`. It is not added to the shared `LmAgentInfra` registry, so CodeReviewDaemon and other consumers acquire no workspace-catalog dependency.

Every operation that needs to create or recreate a remote session—including first message, app restart, provider/mode switch, pool re-creation, or gateway idle-eviction recovery—passes this sample-level gate:

- `incompatible`: fail locally with an actionable workspace compatibility error; do not POST a doomed sandbox-create request.
- `unknown`: return sandbox/gateway unavailable; do not create a remote session.
- `compatible`: continue with existing session creation/recreation.

Already-persisted conversations/history remain readable through REST without a live agent. Resuming one may be blocked if its scoped workspace is now incompatible or the gateway is unavailable; the client must say that the history is readable but remote execution cannot resume until compatibility is restored.

## Client Behavior

The client workspace API consumes the list envelope and enriched workspaces.

State additions:

- Active gateway identity and availability/error.
- Per-workspace compatibility and unsupported aliases.

Behavior:

- Successful list replaces the entire workspace array with the correctly scoped catalog.
- Show the active canonical gateway URL and AppId in the workspace selector/help surface.
- `incompatible` workspace remains visible with unsupported aliases, but cannot be selected for a new Workspace Agent session until edited.
- `unknown` while gateway unavailable remains visible as correctly scoped local metadata, but new Workspace Agent starts and gateway-validated marketplace edits are disabled.
- On `/api/workspaces` network/backend failure (including 502), immediately clear the workspace array and selected ID, then display `Workspace catalog unavailable`. Never retain the previous in-memory list.
- When a selected workspace becomes unavailable/incompatible after refresh, clear the selection or fall back to Default only if Default is compatible; otherwise leave no selection.
- Archived legacy catalogs never appear in the selector.

A gateway change requires app configuration reload/restart. The next process resolves a different scoped catalog automatically.

## Observability and Error Handling

Startup logs at Information level include:

- canonical gateway URL;
- AppId;
- non-secret gateway catalog key prefix;
- scoped catalog path;
- whether legacy migration ran and archive path.

Never log AppKey or hash material containing secrets (AppKey is not part of it).

Marketplace incompatibility logs include workspace ID/name and unsupported aliases. Gateway catalog failure logs include status/category without leaking credentials.

Corrupted scoped catalog behavior becomes fail-safe: log the JSON error, return a catalog-unavailable server error, and do not overwrite the file or present an empty user list. Migration archives use the same strict validation and are never silently treated as empty. This intentionally replaces the current `FileWorkspaceStore` corruption-to-empty behavior because an empty fallback would misleadingly hide gateway-scoped data and permit overwriting it.

## Testing Strategy

Implementation follows RED→GREEN TDD.

### Identity/path tests

- Equivalent URLs produce the same key, including default-port removal and trailing-slash normalization (`/base` == `/base/`, while `/base//` remains distinct after trailing slash removal).
- Different non-default ports, internal path structure, query text/order, or AppIds produce different keys; `localhost` and `127.0.0.1` are intentionally distinct.
- AppKey changes do not affect the key and AppKey is never an input.
- Relative/non-HTTP(S)/user-info URLs reject.
- Manifest mismatch fails without overwrite.

### Persistence/migration tests

- Two gateway identities in one base directory see disjoint catalogs.
- Restart reopens the same scoped catalog.
- Flat catalog is archived and active catalog starts with only Default.
- Migration is idempotent across restarts.
- Simulated interruption before and after the move completes safely from the pending marker, preserving the initiating identity even when current gateway config changes.
- Two concurrent process/store initializers are serialized by the lock file; one migrates and the other observes the completed marker without duplicate archives.
- Existing scoped catalog is never contaminated by a legacy file.
- Archive validation failure stops migration and preserves data.

### Compatibility tests

- Empty aliases compatible through gateway defaults.
- Supported aliases compatible.
- Unsupported aliases incompatible with ordered details.
- Gateway unavailable yields unknown.
- Cache is gateway-instance-local, 30-second bounded, and single-flight.

### Controller/session tests

- List envelope and enriched get shape.
- Create/update 400 on unsupported aliases and 503 on unavailable catalog without persistence changes.
- Session creation blocks locally for incompatible/unknown workspaces and does not call remote sandbox create.
- Compatible workspace reaches existing session creation path.

### Client tests

- Parses list envelope and tracks gateway state.
- Replaces list across different gateway responses.
- Clears workspaces/selection on API/network failure.
- Disables incompatible/unknown starts and shows actionable state.
- Does not show archived legacy records.

### Browser/live verification

- Deterministic browser E2E uses two test gateway identities/catalog roots and proves names do not leak between them.
- Real verification against `http://192.168.11.139:3000`:
  1. upgrade archives flat catalog;
  2. scoped catalog initially shows Default only;
  3. create `LmDotNettools` with remote-supported aliases;
  4. remote sandbox session succeeds;
  5. switch to a second test identity and confirm `LmDotNettools` is absent;
  6. legacy archive remains byte-identical.

## Out of Scope

- Import UI for legacy workspace records.
- Hot gateway switching without app restart.
- Changing remote gateway workspace persistence or per-app rooting.
- Using AppKey in catalog identity.
- Silent marketplace alias migration/fallback.
- Changes from the plural agent-control feature.

## References

- [RFC 3986 URI normalization and comparison](https://www.rfc-editor.org/rfc/rfc3986#section-6)
- [WHATWG URL Standard](https://url.spec.whatwg.org/)
- [NIST FIPS 180-4 SHA-256](https://csrc.nist.gov/pubs/fips/180-4/upd1/final)
- [JSON Schema](https://json-schema.org/)
