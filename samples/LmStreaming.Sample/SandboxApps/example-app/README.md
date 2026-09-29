# Sandbox CGI example

`demo.py` is a request-per-process app. It serves HTML, relative JavaScript and CSS assets, a CSRF-protected form and JavaScript POST, JSON for `fetch()` and XMLHttpRequest, and a response that writes one chunk per second.

For an operator-configured app, place the executable script in a trusted source below the gateway's `PLUGINS_BASE_PATH`. The sandbox session must include a **global** plugin mount named `sandbox-apps`, for example `{"path":"sandbox-apps","name":"sandbox-apps","origin":"global"}` in its `plugins` create request. The gateway mounts that source read-only at `/plugins/sandbox-apps/`; the host executes `/plugins/sandbox-apps/demo.py`. Include Python 3 in the sandbox image. An operator-configured app launch fails closed if its session lacks this mount.

For this sample host, set `SandboxGateway__PluginsBasePath` to the trusted parent directory when it auto-spawns the gateway. Set `SandboxGateway:PluginMounts` to `[ { "Path": "sandbox-apps", "Name": "sandbox-apps", "Origin": "global" } ]`. The file then lives at `<PluginsBasePath>/sandbox-apps/demo.py`. If the host connects to an existing gateway, configure `PLUGINS_BASE_PATH` on that gateway instead. These mount settings apply when a sandbox session is created; existing sessions need replacement before opening the app.

The host feature is off by default. For the development slice, set `SandboxApps__Enabled=true`, `SandboxApps__SameOriginDevelopment=true`, and `SandboxApps__BrowserOrigin` to the browser-facing HTTPS origin, for example `https://site.lvh.me:5011`. The front door may forward over HTTP to this host. The configured origin and `https://localhost` on the same port are accepted development entry points; launch URLs use the origin of the chat request. Localhost requires a direct HTTPS request. The host header is not an identity credential. The app runs under a random `/_mini-app/{instance}/` path on the chat origin. With `Identity__Enforce=false`, it has the same conversation access policy as chat. A gateway agent supporting operation protocol v4 is required.

Same-origin scripts can call the chat page and APIs. The host logs an error on each Mini Web App request so this development risk stays visible. Do not use this mode in production. Mini Web App activation is disabled outside Development and the isolated `MiniAppLocalTest` test environment, even if it is configured. Production front-door authentication and app isolation need a separate review. Add a fixed app entry in host configuration:

```json
{
  "SandboxApps": {
    "Apps": {
      "demo": {
        "Name": "Sandbox demo",
        "Executable": "/plugins/sandbox-apps/demo.py",
        "Arguments": [],
        "WorkingDirectory": "",
        "MaxRequestBytes": 65536,
        "MaxOutputBytes": 8388608,
        "TimeoutSeconds": 30
      }
    }
  }
}
```

The launch ticket and grants live in host memory. Use one host replica or sticky routing for this development slice. Reopening the app mints a new instance. The host passes `REQUEST_METHOD`, `PATH_INFO`, `QUERY_STRING`, `CONTENT_TYPE`, `CONTENT_LENGTH`, `HTTPS`, `SERVER_NAME`, and `SANDBOX_APP_CSRF_TOKEN` to the program. POST forms send `_csrf`; JavaScript POST requests send `X-CSRF-Token`, which is also passed as `HTTP_X_CSRF_TOKEN` after host validation. Use relative browser URLs such as `api/presets`; `/api/presets` would reach chat. The program writes `Status` and `Content-Type`, a blank line, then response bytes. It may also write an app-local `Location` header; the host prefixes that redirect with the app path. No other program response headers are accepted; the host sets cache and security headers.

## LLM-created Mini Web Apps

In a capable workspace, the model creates `mini-web-apps/<app-id>/mini-web-app.json` and an executable entry file in the same directory. The app and its data stay writable in that workspace. The host reads the manifest through the gateway when the client lists apps or opens an app link; no operator catalog edit is needed. For example:

```json
{"id":"budget","name":"Budget Explorer","entry":"app.py","runtime":"python3"}
```

`app.py` needs a Python shebang (`#!/usr/bin/env python3`) and executable permission (`chmod +x app.py`). The host runs `/workspace/mini-web-apps/budget/app.py` inside this conversation's sandbox. It does not execute files from another workspace. The LLM sends `[Open Budget Explorer](#mini-app?workspace=WORKSPACE_ID&app=budget)` using the workspace id supplied by Mini Web App Builder mode. The client resolves that link against the conversation's persisted workspace and receives a typed `kind: mini-web-app` record before opening the app.

The client validates the typed app record before launch. The host checks the conversation workspace, one-use ticket, short-lived path-scoped grant, CSRF token, and gateway capability. These checks apply even when the development chat has no enforced sign-in.
