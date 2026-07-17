# ADR 0001: Route all programmatic sandbox gateway access through the typed SDK

* Status: Accepted
* Date: 2026-07-12
* Related issues, PRs, or commits: #187 (umbrella), #188–#193; gateway ADR 0029 (authenticated app identity and mount authz)

## Context

The sandbox gateway (SandboxedOstoolsMcpServer) exposes a REST + MCP control plane for
sandbox lifecycle, command execution, and file transfer. Before this decision, each consumer
in this repository reached the gateway on its own terms — hand-rolled `HttpClient` calls, ad hoc
header wiring, per-call JSON shapes, and bespoke error handling. That approach spread a set of
security- and correctness-critical invariants across every caller, where each one had to be
re-derived and re-verified:

* **Authentication.** Gateway ADR 0029 authenticates callers by an `X-Sbx-App-Id` /
  `X-Sbx-App-Key` app-identity pair (base64 secret, ≥32 bytes). Every caller had to attach these
  headers correctly and keep the secret out of logs, `ToString()`, exception messages, and URLs.
* **Credential-replay safety.** .NET's automatic-redirect logic re-sends custom headers (including
  these credential headers) to a redirect target; only disabling auto-redirect prevents leaking
  credentials across a `3xx`. This is easy to get wrong per call site.
* **Transport hardening.** A non-loopback address must use HTTPS or credentials go out in the clear.
* **Error taxonomy.** Gateway-controlled error text (e.g. a JSON-RPC `error.message`) is untrusted
  and potentially secret-bearing, so it must never be copied into an exception surfaced to callers;
  failures need a stable, typed classification rather than raw status codes or reflected strings.
* **Wire correctness.** Command output must be reassembled exactly beyond the gateway's `exec`
  truncation, and file transfers must be integrity-verified (whole-file digest, strict UTF-8) — not
  re-implemented per consumer.

Duplicating these concerns is exactly the kind of slow, compounding slippage that produces
divergent, hard-to-audit behavior: one caller forgets to disable redirects, another logs the raw
error body, a third mis-frames the auth headers. The typed `AchieveAi.LmDotnetTools.Sandbox` SDK
(`SandboxClient` + `SandboxClientOptions`) was built in #187 to own all of these concerns in one
place. Issues #191 and #192 then migrated the in-repo consumers (`LmAgentInfra`, `LmStreaming.Sample`,
`CodeReviewDaemon.Sample`) onto it.

## Decision

**All programmatic access to the sandbox gateway MUST go through the typed
`AchieveAi.LmDotnetTools.Sandbox` SDK (`SandboxClient`).** No component may open its own
`HttpClient`/MCP connection to a gateway control-plane endpoint (sandbox lifecycle, catalog/preview,
session discovery, command execution, or file transfer).

Concretely:

* Callers construct a `SandboxClient` from a `SandboxClientOptions(serverAddress, appId,
  clientSecret, …)` — the SDK is the single place that maps `appId`/`clientSecret` to the
  `X-Sbx-App-Id` / `X-Sbx-App-Key` headers, validates the credential, enforces HTTPS-for-non-loopback,
  disables auto-redirect, and redacts the secret.
* New gateway capabilities are surfaced by extending the SDK's typed surface, not by a consumer
  reaching past it to the raw endpoint.
* A caller that needs to share transport passes its own `HttpClient` to the SDK's two-argument
  constructor (subject to the SDK's documented `AllowAutoRedirect = false` precondition); it still
  does not talk to the gateway directly.

Out of scope: this ADR governs *programmatic control-plane* access from code in (or downstream of)
this repository. It does not constrain operators using `curl`/diagnostics against a gateway, nor the
gateway's own internal architecture (owned by the gateway repo's ADRs). It also does **not** cover the
agent-facing MCP **tool-invocation** data plane — an `McpClient` connecting to the gateway's `/mcp`
endpoint to list and call tools is a separate transport with its own concerns and is intentionally
not part of this decision.

## Consequences

* **One audited implementation of the security invariants.** Secret redaction, HTTPS enforcement,
  credential-replay (redirect) protection, and the untrusted-error-text rule are enforced once, in
  the SDK, and covered by its tests and the `AUTH_ENFORCE=true` live contract CI job — instead of
  being re-verified at every call site.
* **Consistent, typed error handling.** Every consumer sees the same `SandboxErrorKind` /
  `SandboxException` taxonomy, so higher layers (e.g. the daemon's `sandbox_auth_failed` vs
  `sandbox_unavailable` distinction) can be built on a stable contract.
* **Central evolution point.** Gateway protocol changes (new headers, wire-shape changes, added
  capabilities) are absorbed in the SDK behind a stable typed API; consumers usually just recompile.
* **A guardrail to maintain.** Reviewers must reject new direct-to-gateway `HttpClient`/MCP code and
  redirect it through the SDK; a capability the SDK does not yet expose becomes a request to extend
  the SDK rather than a reason to bypass it. This is a deliberate, ongoing cost accepted in exchange
  for the invariants above.
* **Migration is complete for current consumers.** `LmAgentInfra` (`SandboxSessionRegistry`),
  `LmStreaming.Sample`, and `CodeReviewDaemon.Sample` route their gateway **control-plane** access
  through the SDK (#191, #192); no known in-repo consumer bypasses it with a hand-rolled control-plane
  `HttpClient` as of this ADR. (The separate agent-facing MCP tool-invocation transport is out of
  scope, per the Decision above.)
