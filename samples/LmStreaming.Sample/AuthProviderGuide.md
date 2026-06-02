# Auth Provider Guide — GitHub + Azure DevOps token injection

This guide covers the **OAuth auth-provider** feature in the `LmStreaming.Sample` app: signing a
user in to GitHub and Azure DevOps with the OAuth **device-code** flow, persisting their refresh
tokens locally, and hosting an **auth webhook** that the sandbox gateway calls to inject a
freshly-refreshed access token into sandbox→GitHub/ADO requests.

It builds directly on the **Workspace Agent** sandbox — read
[`SandboxWorkspaceGuide.md`](./SandboxWorkspaceGuide.md) first if you have not. This guide only
adds the authenticated-egress layer on top of that sandbox.

## Overview

When the workspace agent runs `git` or `curl` against GitHub or Azure DevOps **inside the
sandbox**, the gateway's egress proxy intercepts the outbound request and calls back to this app's
webhook to obtain an `Authorization: Bearer <token>` header to inject. **The sandbox never sees the
token** — it is added by the gateway proxy, outside the sandbox boundary, on the way out.

Key properties:

- **Default-deny egress.** Only the configured GitHub/ADO hosts are reachable from the sandbox, and
  only when the matching provider is signed in. Everything else is blocked.
- **One signed-in identity per provider, app-wide.** There is a single GitHub user and a single ADO
  user for the whole app (not per chat / per session).
- **Refresh tokens are persisted locally** under a gitignored `oauth-tokens/` directory and are
  **never logged**. The app refreshes the short-lived access token on demand from the stored refresh
  token.

```
                         ┌─────────────────────────────────────────────┐
  Browser ── signin ───▶ │  LmStreaming.Sample app                     │
  (device code UI)       │   • AuthController     (api/auth/*)          │
                         │   • AuthWebhookController (api/auth/webhook) │
                         │   • device-code providers + token store      │
                         └───────────────▲─────────────────────────────┘
                                         │ 3. POST /api/auth/webhook/{provider}
                                         │    (shared secret) → Bearer token
                ┌────────────────────────┴──────────────┐
  sandbox ──1. git/curl──▶ │  Sandbox gateway (egress proxy)          │ ──2. allow? + token──▶ GitHub / ADO
  (no token)               └──────────────────────────────────────────┘
```

---

## Prerequisites — register the OAuth apps

This is the part **only you (the developer) can do**: each provider needs an OAuth app registered
on its side, and the app must be configured to issue **refresh tokens**.

### GitHub — you need a GitHub *App*, not a classic OAuth App

> Refresh tokens require a **GitHub App** with token expiry turned on. A *classic OAuth App* issues
> a **non-expiring** `access_token` with **no** `refresh_token` — which mostly works, but you lose
> automatic refresh and the "Expire user authorization tokens" lifecycle.

1. Go to **Settings → Developer settings → GitHub Apps → New GitHub App**.
2. Enable **"Device Flow"** (under the app's general settings).
3. Under **Optional features** / the user-to-server token settings, enable
   **"Expire user authorization tokens"**. This is what makes GitHub issue a `refresh_token` (and an
   `expires_in`) when the user authorizes the device code.
4. Set the user permissions / scopes you need (e.g. *Repository contents: Read-only*).
5. Note the **Client ID** (looks like `Iv1.xxxxxxxxxxxx` or `Iv23xxxxxxxxxxxx`).

Device flow does **not** need a client secret, so you do not have to store one.

### Azure DevOps — register an app in Microsoft Entra ID

1. Go to **Entra ID → App registrations → New registration**.
2. Under **Authentication**, set **"Allow public client flows" = Yes**. This makes it a *public
   client*, so the device-code flow works **without a client secret**.
3. Note the **Application (client) ID** and your **tenant** (a tenant id/domain, or the literal
   `organizations` for work/school accounts).

The app requests the Azure DevOps resource scope `499b84ac-1321-427f-aa17-267ca6975798/.default`
plus `offline_access` (the latter is what makes Entra issue a refresh token). These are already the
defaults in config — you normally only need to supply the **client id** (and possibly the tenant).

---

## Configuration

All settings live under the **`Auth`** configuration section (bound to
`Services/Auth/AuthOptions.cs`). Put non-secret values in `appsettings.json` /
`appsettings.Development.json`, and **secrets** (the gateway shared secret, any client secrets) in
`.env` / user-secrets / environment variables — **never** in committed appsettings.

| Key | Default | Notes |
| --- | --- | --- |
| `Auth:Github:ClientId` | *(empty)* | **Required to enable GitHub.** When empty, GitHub auth is disabled. |
| `Auth:Github:Scopes` | `["repo", "read:org"]` | Scopes requested during the GitHub device-code flow. |
| `Auth:Ado:ClientId` | *(empty)* | **Required to enable ADO.** When empty, ADO auth is disabled. |
| `Auth:Ado:TenantId` | `organizations` | Entra tenant; `organizations` works for work/school accounts. |
| `Auth:Ado:Scopes` | `["499b84ac-1321-427f-aa17-267ca6975798/.default", "offline_access"]` | ADO resource scope + `offline_access` for refresh tokens. |
| `Auth:Webhook:PublicBaseUrl` | `http://127.0.0.1:5000` | The base URL the **gateway** calls back on to reach this app's webhook. |
| `Auth:Webhook:GatewaySharedSecret` | *(empty)* | Shared secret the gateway sends as `Authorization`. If unset, a random 64-hex-char secret is generated at startup. |

### appsettings.json example

```json
{
  "Auth": {
    "Github": {
      "ClientId": "Iv23xxxxxxxxxxxxxxxx",
      "Scopes": ["repo", "read:org"]
    },
    "Ado": {
      "ClientId": "00000000-0000-0000-0000-000000000000",
      "TenantId": "organizations"
    },
    "Webhook": {
      "PublicBaseUrl": "http://127.0.0.1:5000"
    }
  }
}
```

### Environment-variable override form

ASP.NET Core maps nested keys with the double-underscore separator. This is the recommended way to
supply the client ids and the shared secret (e.g. from `.env`):

```bash
Auth__Github__ClientId=Iv23xxxxxxxxxxxxxxxx
Auth__Ado__ClientId=00000000-0000-0000-0000-000000000000
Auth__Ado__TenantId=organizations
Auth__Webhook__GatewaySharedSecret=<a long random string>
```

> **Provider disabled when `ClientId` is empty.** If a provider's client id is blank, that provider
> is simply disabled: **no** `auth_providers`/`network.rules` for it are sent to the gateway, and
> the plain Workspace Agent demo still works unchanged. You can enable just GitHub, just ADO, or
> both.

---

## Sign-in flow (device code) — API

The browser-facing endpoints are served by `Controllers/AuthController.cs` under the route
`api/auth/{provider}` where `{provider}` is **`github`** or **`ado`** (case-insensitive). These
endpoints never return token material — only the device-code challenge and a UI-safe status.

### `POST /api/auth/{provider}/signin`

Starts the device-code flow and returns the challenge to show the user. The app begins polling the
provider's token endpoint **in the background**; once the user approves, the refresh token is stored
automatically.

Response body (`DeviceCodeChallenge`):

```json
{
  "userCode": "WDJB-MJHT",
  "verificationUri": "https://github.com/login/device",
  "verificationUriComplete": "https://github.com/login/device?user_code=WDJB-MJHT",
  "expiresInSeconds": 900,
  "intervalSeconds": 5
}
```

The user opens `verificationUri`, enters `userCode`, and approves. (`verificationUriComplete` is
provided by GitHub but **not** by Entra/ADO, so it may be absent for `ado`.)

> Returns **409 Conflict** if the provider is registered but not configured (e.g. missing
> `ClientId`), and **404** for an unknown provider id.

```bash
curl -s -X POST http://127.0.0.1:5000/api/auth/github/signin | jq
```

### `GET /api/auth/{provider}/status`

Returns the current (UI-safe, secret-free) sign-in status (`OAuthStatus`):

```json
{
  "state": "SignedIn",
  "account": "octocat",
  "scopes": ["repo", "read:org"],
  "expiresAtUtc": "2026-06-01T12:34:56+00:00",
  "error": null
}
```

`state` is one of `NotStarted` | `Pending` | `SignedIn` | `Failed`. Poll this while the user
authorizes the device code; `Pending` → `SignedIn` once approval lands, or `Failed` (with `error`)
if it expires / is declined. (`account` may be `null` for ADO — Entra access tokens are opaque and
not parsed.)

```bash
curl -s http://127.0.0.1:5000/api/auth/github/status | jq
```

### `POST /api/auth/{provider}/signout`

Clears the stored tokens and resets the provider to `NotStarted`. Returns **204 No Content**.

```bash
curl -s -X POST http://127.0.0.1:5000/api/auth/github/signout -o /dev/null -w "%{http_code}\n"
```

The same three endpoints exist for `ado` — just swap `github` for `ado` in the path.

---

## How injection works (the webhook)

The webhook contract is implemented by `Controllers/AuthWebhookController.cs` on the route
`api/auth/webhook/{provider}`.

1. The gateway calls **`POST {PublicBaseUrl}/api/auth/webhook/{github|ado}`** whenever a sandboxed
   outbound request matches a rule that requires that provider's credential.
2. The request is **authenticated with the shared secret** carried in the `Authorization` header.
   The controller compares it to the configured/generated secret in **constant time** (both sides
   are SHA-256 hashed, then compared with `CryptographicOperations.FixedTimeEquals`), so neither the
   length nor the value leaks via timing. A mismatch returns **401**.
3. On success the app returns an **allow** decision with a freshly-valid bearer token and the
   token's **real expiry**:

   ```json
   {
     "decision": "allow",
     "headers": [["Authorization", "Bearer <token>"]],
     "expires_at": "2026-06-01T12:34:56+00:00"
   }
   ```

   If the provider is not signed in (or a refresh fails), it returns a **deny** decision carrying a
   token-free reason:

   ```json
   { "decision": "deny", "reason": "no valid token for provider 'github'; sign in required" }
   ```

   Both outcomes return **HTTP 200** — the decision lives in the body, not the status code.
4. The gateway **caches the token by its `expires_at`** and re-calls the webhook when it lapses. On
   the re-call the app transparently **refreshes** the access token using the stored refresh token
   (the access token is renewed ~120s before its real expiry), persists the new value, and returns
   it. The webhook never echoes token material into the gateway's logs.

### What the sandbox is created with

When at least one provider is configured, `Services/SandboxSessionRegistry.cs` adds two blocks to
the sandbox-create request:

- **`auth_providers`** — one `webhook`-type provider per configured OAuth provider, each pointing at
  `{PublicBaseUrl}/api/auth/webhook/{provider}`, carrying the shared secret as `gateway_auth`, with
  a 300-second cache TTL.
- **`network.rules`** — default-deny by virtue of being an explicit allowlist; each rule has
  `action: "allow"` and routes that provider's hosts (port 443) through the matching auth provider:
  - **github** → `github.com`, `api.github.com`, `codeload.github.com`
  - **ado** → `dev.azure.com`, `*.dev.azure.com`, `*.visualstudio.com`

When **no** provider is configured, both blocks are omitted entirely and the plain workspace sandbox
is created as before.

### Loopback / SSRF allowlist

The gateway refuses to call an HTTP loopback webhook by default (SSRF protection). The app
**auto-spawns** the gateway with `AUTH_WEBHOOK_HTTP_LOOPBACK_HOSTS=127.0.0.1,localhost` set, which
allowlists the loopback host so the `http://127.0.0.1:5000` callback is permitted. If you run your
own gateway, you must set this yourself (or serve the webhook over HTTPS).

---

## Try it (live token-injection test)

1. **Configure a client id.** Set `Auth__Github__ClientId` (and/or `Auth__Ado__ClientId`), e.g. in
   `.env`. For ADO also set the tenant if it is not `organizations`.
2. **Run the app** (this also auto-spawns the gateway with the loopback allowlist set):

   ```bash
   dotnet run --project samples/LmStreaming.Sample
   ```

3. **Start sign-in** and read back the challenge:

   ```bash
   curl -s -X POST http://127.0.0.1:5000/api/auth/github/signin | jq
   ```

4. **Approve in the browser** — open `verificationUri`, enter the `userCode`, approve.
5. **Wait for `SignedIn`:**

   ```bash
   curl -s http://127.0.0.1:5000/api/auth/github/status | jq .state
   # "Pending" ... then "SignedIn"
   ```

6. **Exercise injection from inside the sandbox.** In the Workspace Agent chat, ask the model to run
   one of these via `Bash`:

   - GitHub:

     ```bash
     curl -s https://api.github.com/user
     ```

   - Azure DevOps:

     ```bash
     curl -s "https://dev.azure.com/<org>/_apis/projects?api-version=7.1"
     ```

   The call **succeeds** because the gateway injected the bearer token on the way out — even though
   the sandbox never received it.
7. **Confirm the token is NOT visible inside the sandbox.** Ask the model to print the request
   headers it sent, or to dump its environment — you will see **no** token. The `Authorization`
   header is added by the gateway proxy *after* the request leaves the sandbox.
8. **Force a refresh.** Wait past the access token's lifetime (or past the gateway's cache TTL) and
   repeat step 6. The gateway re-calls the webhook, the app refreshes from the stored refresh token,
   and the call still succeeds — no re-sign-in required.

---

## Security notes

- **Refresh tokens and the shared secret are sensitive.** Refresh/access tokens are persisted as
  one JSON file per provider under the gitignored `oauth-tokens/` directory (matched by
  `**/oauth-tokens/` in `.gitignore`). Token material is **never** written to logs — only the
  provider id, account, scopes, and expiry.
- **Webhook authentication is constant-time.** The shared secret is validated with a fixed-time
  comparison of SHA-256 digests; the controller never logs the token, the incoming `Authorization`
  value, or the secret, and never echoes token material back to the gateway.
- **Default-deny egress.** With auth configured, only the authenticated GitHub/ADO hosts are
  reachable from the sandbox. Anything else is blocked by the absence of an `allow` rule.
- **Keep the shared secret out of source control.** Supply
  `Auth__Webhook__GatewaySharedSecret` via `.env`/user-secrets/environment. If you leave it unset, a
  random secret is generated per process start (fine for local dev, but it changes on each restart).

---

## Troubleshooting

**GitHub status never reaches `SignedIn`, or there is no refresh token.**
You almost certainly registered a *classic OAuth App* instead of a **GitHub App** with **"Expire
user authorization tokens"** enabled. Classic OAuth Apps do not issue a `refresh_token`. Re-register
as a GitHub App with Device Flow + token expiry enabled (see Prerequisites).

**The gateway rejects the webhook endpoint (SSRF / loopback refused).**
The gateway will not call an HTTP loopback URL unless it is allowlisted. Make sure the gateway was
**spawned by the app** (so `AUTH_WEBHOOK_HTTP_LOOPBACK_HOSTS=127.0.0.1,localhost` is set), or — if
you run your own gateway — that the `PublicBaseUrl` host is in that env var, or serve the webhook
over **HTTPS**.

**401 from the webhook.**
The shared secret the gateway sends does not match the app's. Ensure
`Auth__Webhook__GatewaySharedSecret` is the same value the gateway uses — and remember that if you
leave it unset, the app generates a **new random secret on every restart**, so a previously-spawned
gateway will be out of sync. Set an explicit secret to pin it.

**`deny` with "sign in required" in the webhook response.**
The provider is not signed in (or its refresh failed). Re-run the sign-in flow and confirm
`GET /api/auth/{provider}/status` returns `SignedIn` before exercising the sandbox.
