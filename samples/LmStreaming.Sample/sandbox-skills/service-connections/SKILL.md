---
name: service-connections
description: How to connect to and authenticate with external services (APIs) from inside this sandbox. Read this whenever a task needs an authenticated external service — a code host, an issue tracker, a cloud API, etc. It explains how authentication works here (the sandbox injects credentials for you), how to initiate a connection, and exactly what to expect — including what to do when access is denied.
---

# Connecting to external services

You do **not** manage credentials in this sandbox. Authentication to supported external services is
handled **transparently by the egress proxy**: when you make an outbound HTTPS request to a connected
service, the proxy attaches the signed-in user's freshly-refreshed OAuth token for you. You never see,
request, store, or send a token yourself.

This skill is generic — the same model applies to every connected service. Specific services (which
hosts are reachable, which scopes are granted) are decided by the app operator and by what the user
has signed in to; treat the examples here as illustrations of the pattern, not the whole list.

## How to initiate a connection

There is no "connect" or "login" step for you to perform. To use a service, simply **call its REST/HTTPS
API the normal way** from the shell:

```bash
curl -fsS https://<service-host>/<api-path>
```

That's it. If the service is connected and the user is signed in, the request succeeds with the user's
identity. The "connection" is established implicitly by the request itself.

## What to expect

- **No credentials from you.** Do **not** add an `Authorization` header, API key, token, or PAT, and do
  **not** ask the user for one. The proxy injects the bearer; adding your own auth will *break* it.
- **Allowlisted hosts only.** Only services the operator configured are reachable; every other host is
  **denied by default**. A request to an unconfigured host will simply fail/refuse — that is expected,
  not a bug.
- **Tokens are invisible and auto-refreshed.** The injected token is never exposed to you or to the
  workspace, and it is refreshed automatically when it expires. Don't try to read it from headers or env.
- **Identity = the signed-in user.** Calls act as whoever signed in through the app, with the scopes
  they granted. A `403`/insufficient-scope means they need to re-consent with broader permissions.
- **Read vs write.** Reads are safe to run. For state-changing calls (create/update/delete), confirm the
  intent with the user first, then use the matching `POST`/`PATCH`/`PUT`/`DELETE` — still with no auth header.
- **TLS through the proxy.** Egress is transparently MITM'd by a proxy using a private CA. On Windows,
  native `curl` uses schannel and cannot do a revocation check on that private CA, so pass
  **`--ssl-no-revoke`** (e.g. `curl --ssl-no-revoke -fsS ...`). If you instead see a certificate-trust
  error, the CA bundle is already exported via `CURL_CA_BUNDLE`/`SSL_CERT_FILE` for OpenSSL-based tools.

## When access is denied (401 / 403 / connection blocked)

This almost always means **the user has not signed in to that service yet** (or the service isn't
connected). Do **not** retry in a loop, fabricate credentials, or try alternate hosts. Instead, stop and
tell the user plainly, e.g.:

> "To do this I need access to **<service>**. Please sign in from the app — start the **<service>** sign-in;
> a browser window will open where you log in and authorize. Then ask me again."

Then wait. Once they've signed in, the same request will succeed.

## Procedure

1. **Identify the service and the exact API resource** the task needs.
2. **Verify reachability if unsure** with a cheap identity/metadata call (e.g. a "who am I" or a metadata
   endpoint). If it’s denied, follow *When access is denied* above and stop.
3. **Make the real call** with `curl` (or the workspace's HTTP tooling) — **no auth header**.
4. **Summarize** only the fields the user asked for; don't dump entire payloads, and never echo headers
   that might carry injected auth.
5. **For writes**, confirm first, then call the appropriate endpoint.

## Illustrative examples (the pattern, not the full catalog)

```bash
# A code host (e.g. GitHub): identity check, then repo metadata
# (--ssl-no-revoke is for Windows schannel curl against the proxy's private CA)
curl --ssl-no-revoke -fsS https://api.github.com/user
curl --ssl-no-revoke -fsS https://api.github.com/repos/<owner>/<repo>

# A cloud DevOps API (e.g. Azure DevOps): note the api-version query param + org scoping
curl --ssl-no-revoke -fsS "https://dev.azure.com/<org>/_apis/projects?api-version=7.1"
```

If you don't know an org/project/owner, ask the user — that's data you need, not a credential.
