# Auth Provider Guide — GitHub + Azure DevOps + Microsoft 365 token injection

This guide covers the **OAuth auth-provider** feature in the `LmStreaming.Sample` app: signing a
user in to GitHub, Azure DevOps and Microsoft 365 (Microsoft Graph) with an interactive
authorization-code flow (open the system browser, log in, the app captures the redirect), persisting
their tokens locally, and hosting an **auth webhook** that the sandbox gateway calls to inject a
freshly-valid access token into sandbox→GitHub / ADO / Graph requests.

> **Flow per provider.** GitHub uses the OAuth *web-application* (authorization-code) flow — the same
> one the GitHub CLI uses; it needs a client **secret** in the code→token exchange (GitHub requires
> it even with PKCE). The callback lands on an **ephemeral loopback** port (`http://127.0.0.1:<port>/callback`).
> Azure DevOps uses **MSAL.NET** (`AcquireTokenInteractive`/`AcquireTokenSilent`) against Microsoft
> Entra — a public client with **no secret** and PKCE, mirroring how the `azure-devops-mcp`
> authenticates. The MSAL public-client flow also owns its own loopback listener.
> Microsoft 365 (Graph) uses **MSAL.NET confidential-client + PKCE** with the callback hosted on
> **the app's primary port** (`http://localhost:5000/auth/m365/callback`) — no standalone
> `HttpListener`, no fixed port 3333. A **client secret is required** (the Entra app is a web app),
> and it MUST come from user-secrets / env (`Auth__M365__ClientSecret`), never from committed
> appsettings.
> Because the app's backend runs on your own machine, "open the browser" happens locally; a
> headless/remote host is not supported by this sample.

It builds directly on the **Workspace Agent** sandbox — read
[`SandboxWorkspaceGuide.md`](./SandboxWorkspaceGuide.md) first if you have not. This guide only
adds the authenticated-egress layer on top of that sandbox.

## Overview

When the workspace agent runs `git`, `curl`, or any HTTP call against GitHub, Azure DevOps or
Microsoft Graph **inside the sandbox**, the gateway's egress proxy intercepts the outbound request
and calls back to this app's webhook to obtain an `Authorization: Bearer <token>` header to inject.
**The sandbox never sees the token** — it is added by the gateway proxy, outside the sandbox
boundary, on the way out.

Key properties:

- **Default-deny egress.** Only the configured GitHub/ADO/M365 hosts are reachable from the sandbox,
  and only when the matching provider is signed in. Everything else is blocked.
- **One signed-in identity per provider, app-wide.** There is a single GitHub user, a single ADO
  user and a single M365 user for the whole app (not per chat / per session).
- **Refresh tokens are persisted locally** under a gitignored `oauth-tokens/` directory and are
  **never logged**. The app refreshes the short-lived access token on demand from the stored refresh
  token (GitHub: `github.json`; ADO/M365: MSAL cache files `msal-ado.bin` / `msal-m365.bin`).

```
                         ┌──────────────────────────────────────────────────┐
  Browser ◀─ opens ────▶ │  LmStreaming.Sample app                          │
  (login + redirect      │   • AdoAuthController       (api/auth/ado/*)     │
   to loopback OR the    │   • GitHubAuthController    (api/auth/github/*)  │
   app's primary port)   │   • M365AuthController      (api/auth/m365/*     │
                         │                              + auth/m365/callback)│
                         │   • AuthPagesController     (GET /auth/{prov})   │
                         │   • AuthWebhookController   (api/auth/webhook/*) │
                         │   • GitHub web-flow + ADO MSAL + M365 MSAL CCA   │
                         └────────────────────▲─────────────────────────────┘
                                              │ 3. POST /api/auth/webhook/{provider}
                                              │    (shared secret) → Bearer token
                ┌─────────────────────────────┴───────────┐
  sandbox ──1. git/curl──▶ │  Sandbox gateway (egress proxy)            │ ──2. allow? + token──▶ GitHub / ADO / Graph
  (no token)               └────────────────────────────────────────────┘
```

---

## Prerequisites — the OAuth apps

By default the sample is **ready to run with no registration**: it ships with the first-party client
ids (and, for GitHub, the published client secret) of the GitHub CLI and the `azure-devops-mcp`
public client, so you can sign in with a browser immediately. Register your own apps only if you
prefer not to reuse those.

### GitHub — reuse the CLI app, or register your own

The default config uses the **GitHub CLI's** OAuth App id + secret. GitHub publishes both in the
`gh` source (`internal/authflow/flow.go`) annotated *"safe to be embedded in version control"*,
because the web-application flow requires the secret in the code→token exchange (GitHub mandates it
even with PKCE — it does not distinguish public vs confidential clients). The CLI app is a **classic
OAuth App**, so its tokens are **non-expiring** with no `refresh_token` — which is fine: there is
nothing to refresh and `GetAccessTokenAsync` just returns the stored token.

To register your own instead:

1. Go to **Settings → Developer settings → OAuth Apps → New OAuth App** (or a **GitHub App** if you
   want expiring tokens + refresh).
2. Set the **Authorization callback URL** to `http://127.0.0.1/callback` (GitHub permits a dynamic
   loopback port, so the app's ephemeral `http://127.0.0.1:<port>/callback` matches).
3. Note the **Client ID** and generate a **Client Secret** — both are required.
4. (GitHub App only) enable **"Expire user authorization tokens"** to get an expiring `access_token`
   + `refresh_token`; the provider handles the refresh-token grant automatically.

### Azure DevOps — reuse the MCP public client, or register your own

The default config uses the **`azure-devops-mcp` public client** id with authority `common`. ADO
sign-in goes through **MSAL.NET**, which owns the loopback listener, PKCE, token cache and silent
refresh — **no client secret**. To register your own:

1. Go to **Entra ID → App registrations → New registration**.
2. Under **Authentication**, add a **"Mobile and desktop applications"** platform with redirect URI
   `http://localhost`, and set **"Allow public client flows" = Yes**.
3. Note the **Application (client) ID** and your **tenant** (`organizations` for work/school
   accounts, or a specific tenant id/domain).

The app requests the Azure DevOps resource scope `499b84ac-1321-427f-aa17-267ca6975798/.default`.
`offline_access` may appear in config for parity, but MSAL manages refresh itself and the provider
strips reserved scopes before calling MSAL.

### Microsoft 365 — register your own confidential client

M365 (Microsoft Graph) uses an **Entra confidential web client** with MSAL.NET, authorization-code +
PKCE. Unlike GitHub/ADO there is **no default first-party app shipped in config** — you must
register your own and supply the client secret out of band.

1. Go to **Entra ID → App registrations → New registration**. Pick **"Accounts in any organizational
   directory (Multitenant) — personal Microsoft accounts not required"** unless you want a single-tenant
   pin.
2. Under **Authentication**, add a **"Web"** platform with redirect URI
   `http://localhost:5000/auth/m365/callback` — this is hosted on the **app's primary port**, not on
   an ephemeral loopback. If you change the app's port or the `Auth:M365:RedirectPath` setting, the
   redirect URI registered in Entra must match exactly.
3. Under **Certificates & secrets**, create a **client secret**. Copy the **Value** (not the Id) —
   you'll only see it once. Supply it via user-secrets / env (`Auth__M365__ClientSecret`); never
   commit it to source.
4. Note the **Application (client) ID** and the **Directory (tenant) ID**. The sample defaults the
   tenant to `common` (multi-tenant); pin to your tenant id if you want per-tenant cache isolation.
5. Under **API permissions**, add the **delegated Microsoft Graph** permissions you intend to
   request. The defaults (`User.Read`, `Mail.Read`, `Calendars.Read`, `OnlineMeetings.Read`) require
   nothing but user consent; resource-targeting Teams scopes (`OnlineMeetings.ReadWrite`,
   `ChannelMessage.Read.All`, …) require **admin consent** and only work for accounts in the
   tenant that granted it.

> **Important — organizer-only Teams meetings.** Microsoft Graph's `OnlineMeetings.Read` (delegated)
> only returns meetings the **signed-in user organized**. Reading another organizer's meetings
> requires application permissions + admin consent, which is out of scope for this sample.

---

## Configuration

All settings live under the **`Auth`** configuration section (bound to
`Services/Auth/AuthOptions.cs`). Put non-secret values in `appsettings.json` /
`appsettings.Development.json`, and **secrets** (the gateway shared secret, any client secrets) in
`.env` / user-secrets / environment variables — **never** in committed appsettings.

| Key | Default | Notes |
| --- | --- | --- |
| `Auth:Github:ClientId` | *(empty; Dev: gh CLI id)* | **Required to enable GitHub.** When empty, GitHub auth is disabled. |
| `Auth:Github:ClientSecret` | *(empty; Dev: gh CLI secret)* | **Required to enable GitHub.** GitHub needs the secret in the code→token exchange. The gh CLI's published secret is the Dev default. |
| `Auth:Github:Scopes` | `["repo", "read:org"]` | Scopes requested during the GitHub authorization-code flow. |
| `Auth:Ado:ClientId` | *(empty; Dev: azure-devops-mcp id)* | **Required to enable ADO.** When empty, ADO auth is disabled. No secret (public client). |
| `Auth:Ado:TenantId` | `organizations` | Entra tenant; `organizations` works for work/school accounts. |
| `Auth:Ado:Scopes` | `["499b84ac-1321-427f-aa17-267ca6975798/.default", "offline_access"]` | ADO resource scope. MSAL manages refresh; reserved scopes (e.g. `offline_access`) are stripped before MSAL. |
| `Auth:M365:ClientId` | *(empty)* | **Required to enable M365.** When empty, M365 auth is disabled. No first-party default — register your own Entra confidential client. |
| `Auth:M365:ClientSecret` | *(empty)* | **REQUIRED to enable M365** and **MUST come from user-secrets / env only** (`Auth__M365__ClientSecret`), never from committed appsettings. |
| `Auth:M365:TenantId` | `common` | Entra tenant; `common` works for multi-tenant work/school accounts. Pin to a tenant id for per-tenant token cache isolation. |
| `Auth:M365:Scopes` | `["User.Read","Mail.Read","Calendars.Read","OnlineMeetings.Read"]` | Delegated Microsoft Graph scopes. MSAL strips reserved OIDC scopes (`openid`/`profile`/`offline_access`) before calling MSAL. |
| `Auth:M365:RedirectPath` | `/auth/m365/callback` | App-primary-port callback path. Must exactly match the redirect URI registered in the Entra app. |
| `Auth:Webhook:PublicBaseUrl` | `http://127.0.0.1:5000` | The base URL the **gateway** calls back on to reach this app's webhook. Also serves as the M365 callback base. |
| `Auth:Webhook:GatewaySharedSecret` | *(empty)* | Shared secret the gateway sends as `Authorization`. If unset, a random 64-hex-char secret is generated at startup. |
| `Auth:Webhook:HoldTimeoutSeconds` | `120` | Deferred auth: how long a not-signed-in webhook call is held open while the chat UI prompts for sign-in. `0` disables deferral (immediate deny). |
| `Auth:Webhook:PollIntervalSeconds` | `1.0` | Deferred auth: interval between token-acquisition attempts while a webhook call is held. |

### appsettings.json example

```json
{
  "Auth": {
    "Github": {
      "ClientId": "Iv23xxxxxxxxxxxxxxxx",
      "ClientSecret": "<your github client secret>",
      "Scopes": ["repo", "read:org"]
    },
    "Ado": {
      "ClientId": "00000000-0000-0000-0000-000000000000",
      "TenantId": "organizations"
    },
    "M365": {
      "ClientId": "00000000-0000-0000-0000-000000000000",
      "TenantId": "common",
      "Scopes": ["User.Read", "Mail.Read", "Calendars.Read", "OnlineMeetings.Read"]
    },
    "Webhook": {
      "PublicBaseUrl": "http://127.0.0.1:5000"
    }
  }
}
```

> The checked-in `appsettings.Development.json` already supplies the GitHub CLI id+secret and the
> `azure-devops-mcp` client id, so `dotnet run` works without any of the above.

### Environment-variable override form

ASP.NET Core maps nested keys with the double-underscore separator. This is the recommended way to
supply your own client ids/secret and the shared secret (e.g. from `.env`):

```bash
Auth__Github__ClientId=Iv23xxxxxxxxxxxxxxxx
Auth__Github__ClientSecret=<your github client secret>
Auth__Ado__ClientId=00000000-0000-0000-0000-000000000000
Auth__Ado__TenantId=organizations
Auth__M365__ClientId=00000000-0000-0000-0000-000000000000
Auth__M365__ClientSecret=<your m365 entra confidential-client secret>
Auth__M365__TenantId=common
Auth__Webhook__GatewaySharedSecret=<a long random string>
```

> **Provider disabled when client id (or, for M365, the client secret) is empty.** That provider is
> simply disabled: **no** `auth_providers`/`network.rules` for it are sent to the gateway, and the
> plain Workspace Agent demo still works unchanged. You can enable any subset of GitHub, ADO, and
> M365 independently.

---

## Sign-in flow — API

Each provider gets its own thin controller under `Controllers/` — `AdoAuthController`,
`GitHubAuthController`, `M365AuthController` — routed at `api/auth/{provider}/*` where `{provider}`
is **`github`**, **`ado`**, or **`m365`** (case-insensitive). These endpoints never return token
material — only the sign-in challenge and a UI-safe status.

> **Where the callback lands.** GitHub and ADO use a per-process ephemeral **loopback** redirect
> (`http://127.0.0.1:<port>/callback`) owned by their respective sign-in machinery. M365 instead
> hosts the callback on the **app's primary port** (`GET /auth/m365/callback`, served by
> `M365AuthController`) — this matches Entra's "Web" client expectations and avoids a standalone
> `HttpListener`.

### Browser entry — `GET /auth/{provider}`

For convenience there is also a tiny HTML landing page at `GET /auth/{provider}` (served by
`AuthPagesController`). Visiting it in a browser either renders the already-signed-in status or
starts the interactive sign-in (opens the system browser) and returns a page that polls
`/api/auth/{provider}/status` until the flow completes. Unknown provider ids return **404**.

### `POST /api/auth/{provider}/signin`

**Opens the system browser** to the provider's login page and returns immediately. The
authorization-code exchange completes **in the background** on a loopback redirect
(`http://127.0.0.1:<port>/callback`); once the user finishes signing in, the token is stored
automatically and the status flips to `SignedIn`.

Response body (`SignInChallenge`):

```json
{
  "authorizationUrl": "https://github.com/login/oauth/authorize?client_id=...&redirect_uri=http%3A%2F%2F127.0.0.1%3A53117%2Fcallback&scope=repo%20read%3Aorg&state=...&code_challenge=...&code_challenge_method=S256",
  "browserLaunched": true
}
```

`authorizationUrl` is the URL that was opened — surface it so the user can click it manually if
`browserLaunched` is `false` (e.g. no default browser). There is no code to type: the user just logs
in and the loopback redirect does the rest.

> Returns **409 Conflict** if the provider is registered but not configured (e.g. missing `ClientId`
> / GitHub `ClientSecret`), and **404** for an unknown provider id.

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
completes sign-in in the browser; `Pending` → `SignedIn` once the loopback exchange lands, or
`Failed` (with `error`) if it is declined / the state check fails. (`account` is the GitHub login;
for ADO it is the Entra account username.)

```bash
curl -s http://127.0.0.1:5000/api/auth/github/status | jq
```

### `POST /api/auth/{provider}/signout`

Clears the stored tokens and resets the provider to `NotStarted`. Returns **204 No Content**.

```bash
curl -s -X POST http://127.0.0.1:5000/api/auth/github/signout -o /dev/null -w "%{http_code}\n"
```

The same three endpoints exist for `ado` and `m365` — just swap `github` for `ado` or `m365` in
the path. The M365 controller additionally serves the Entra-facing callback at
`GET /auth/m365/callback` (no `/api` prefix — that path is the redirect URI registered with Entra).

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

   If the provider is not signed in (or a refresh fails), the call is first **held for deferred
   auth** (see below); only when that hold resolves without a token does the app return a **deny**
   decision carrying a token-free reason:

   ```json
   { "decision": "deny", "reason": "no valid token for provider 'github'; sign in required" }
   ```

   Both outcomes return **HTTP 200** — the decision lives in the body, not the status code.
4. The gateway **caches the token by its `expires_at`** and re-calls the webhook when it lapses. On
   the re-call the app transparently **refreshes** the access token using the stored refresh token
   (the access token is renewed ~120s before its real expiry), persists the new value, and returns
   it. The webhook never echoes token material into the gateway's logs.

### Deferred auth (sign in on demand — no pre-auth required)

You no longer need to sign in *before* the sandboxed agent touches GitHub/ADO/Graph. When the
webhook finds no valid token, `Services/Auth/PendingAuthCoordinator.cs` **holds the gateway's call
open** instead of denying:

1. Connected chat clients receive an out-of-band WebSocket frame
   (`{"$type":"auth_required","providerId":"github","signinUrl":"/auth/github","reason":"..."}`)
   and the chat UI shows a sign-in banner. Clients that connect *while* a hold is in flight get the
   prompt replayed on connect. One prompt is broadcast per provider no matter how many webhook
   calls are held.
2. Clicking the banner's **Sign in** button opens the `/auth/{provider}` landing page in a popup,
   which starts the normal interactive sign-in.
3. The hold **polls the provider's token acquisition** (every `Auth:Webhook:PollIntervalSeconds`)
   — the moment a token lands (sign-in completed and persisted), the held call resolves **allow**
   with the injected header, and clients receive `{"$type":"auth_completed","providerId":"github"}`
   to dismiss the banner.
4. If the user never signs in, the hold resolves **deny** after `Auth:Webhook:HoldTimeoutSeconds`
   (default **120**). A *fresh* sign-in failure during the hold denies early; a stale `Failed`
   status from an earlier attempt does not block deferral. Aborted gateway calls release the hold
   immediately.

Configuration:

| Key | Default | Meaning |
| --- | --- | --- |
| `Auth:Webhook:HoldTimeoutSeconds` | `120` | How long a not-signed-in webhook call is held. **Set to `0` to disable deferral** (restore immediate deny). Keep below the gateway's own webhook client timeout. |
| `Auth:Webhook:PollIntervalSeconds` | `1.0` | Interval between token-acquisition attempts while holding. |

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
  - **m365** → `graph.microsoft.com`

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

3. **Start sign-in** — this opens your browser to the provider's login page:

   ```bash
   curl -s -X POST http://127.0.0.1:5000/api/auth/github/signin | jq
   ```

4. **Sign in in the browser** that just opened. There is no code to type — just log in and
   authorize; the loopback redirect completes the exchange and you'll see a "Sign-in complete" tab.
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

- **Tokens and the shared secret are sensitive.** GitHub tokens are persisted as one JSON file per
  provider; ADO and M365 use **MSAL token caches** (containing their refresh tokens) at
  `msal-ado.bin` and `msal-m365.bin` respectively. All three live under the gitignored
  `oauth-tokens/` directory (matched by `**/oauth-tokens/` in `.gitignore`). These blobs are
  plaintext at rest — acceptable for local dev; harden with OS-protected storage for production.
  Token material is **never** written to logs — only the provider id, account, scopes, and expiry.
- **M365 client secret stays out of source.** The M365 confidential-client secret MUST be supplied
  via user-secrets / env (`Auth__M365__ClientSecret`). Committed `appsettings.Development.json`
  carries the M365 client id + tenant only — no secret.
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

**GitHub sign-in returns 409 Conflict.**
The GitHub provider needs **both** a `ClientId` and a `ClientSecret` (the web-application flow can't
exchange the code without the secret). The Dev config supplies the gh CLI's published pair; if you
override the id, set the matching secret too.

**The browser didn't open / nothing happened after `signin`.**
Check `browserLaunched` in the response. If `false`, open the `authorizationUrl` yourself. The
backend opens the browser on *its own machine* — this sample assumes the backend runs locally on
your workstation, so a headless/remote host won't show a browser.

**GitHub has no refresh token (token never expires).**
Expected for the default (the gh CLI app is a *classic OAuth App* — non-expiring `access_token`, no
`refresh_token`). There is nothing to refresh; the stored token is returned as-is. Register a
**GitHub App** with **"Expire user authorization tokens"** if you specifically want the refresh
lifecycle.

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

**M365 sign-in fails immediately or returns 409 Conflict.**
The M365 provider needs **both** a `ClientId` and a `ClientSecret` (it's a confidential-client web
app). With either missing, sign-in is disabled and POST `/api/auth/m365/signin` returns 409.
Supply the secret via `Auth__M365__ClientSecret` only (user-secrets / env), never via committed
appsettings.

**M365 callback returns `AADSTS50011: reply URL does not match`.**
The redirect URI registered in the Entra app must **exactly** match
`{Auth:Webhook:PublicBaseUrl}{Auth:M365:RedirectPath}` (default
`http://localhost:5000/auth/m365/callback`). If you change the app port or the `RedirectPath`,
update the Entra app's redirect URI to match.

**M365 returns empty results for another organizer's Teams meetings.**
Expected. Delegated `OnlineMeetings.Read` only sees meetings the **signed-in user** organized;
reading another organizer's meetings needs application permissions + admin consent (out of scope
for this sample).
