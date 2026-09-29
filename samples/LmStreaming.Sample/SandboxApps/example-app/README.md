# Sandbox CGI example

`demo.py` is a request-per-process app. It serves HTML, relative JavaScript and CSS assets, a CSRF-protected form and JavaScript POST, JSON for `fetch()` and XMLHttpRequest, and a response that writes one chunk per second.

For an operator-configured app, place the executable script in a trusted source below the gateway's `PLUGINS_BASE_PATH`. The sandbox session must include a **global** plugin mount named `sandbox-apps`, for example `{"path":"sandbox-apps","name":"sandbox-apps","origin":"global"}` in its `plugins` create request. The gateway mounts that source read-only at `/plugins/sandbox-apps/`; the host executes `/plugins/sandbox-apps/demo.py`. Include Python 3 in the sandbox image. An operator-configured app launch fails closed if its session lacks this mount.

For this sample host, set `SandboxGateway__PluginsBasePath` to the trusted parent directory when it auto-spawns the gateway. Set `SandboxGateway:PluginMounts` to `[ { "Path": "sandbox-apps", "Name": "sandbox-apps", "Origin": "global" } ]`. The file then lives at `<PluginsBasePath>/sandbox-apps/demo.py`. If the host connects to an existing gateway, configure `PLUGINS_BASE_PATH` on that gateway instead. These mount settings apply when a sandbox session is created; existing sessions need replacement before opening the app.

The host feature is off by default. On an HTTPS deployment, set `SandboxApps__Enabled=true`, `SandboxApps__SiteDomain` to the shared site (for example `example.com`), and `SandboxApps__AppDomain` to a dedicated wildcard domain (for example `apps.example.com`). Route `*.apps.example.com` to the same host with wildcard TLS. The chat origin must be a different hostname below `SiteDomain`. Set `Identity__Enforce=true` and configure the existing sign-in path. Add this fixed app entry in host configuration:

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

The launch ticket and grants live in host memory. Use one host replica or sticky routing for the first release. Reopening the app mints a new instance. The host passes `REQUEST_METHOD`, `PATH_INFO`, `QUERY_STRING`, `CONTENT_TYPE`, `CONTENT_LENGTH`, `HTTPS`, `SERVER_NAME`, and `SANDBOX_APP_CSRF_TOKEN` to the program. POST forms send `_csrf`; JavaScript POST requests send `X-CSRF-Token`, which is also passed as `HTTP_X_CSRF_TOKEN` after host validation. App paths such as `/api/presets` are routed to the app, never to the chat API. The program writes `Status` and `Content-Type`, a blank line, then response bytes. It may also write an app-local `Location` header. No other program response headers are accepted; the host sets cache and security headers.

## LLM-created Mini Web Apps

In a capable workspace, the model creates `mini-web-apps/<app-id>/mini-web-app.json` and an executable entry file in the same directory. The app and its data stay writable in that workspace. The host reads the manifest through the gateway when the client lists apps or opens an app link; no operator catalog edit is needed. For example:

```json
{"id":"budget","name":"Budget Explorer","entry":"app.py","runtime":"python3"}
```

`app.py` needs a Python shebang (`#!/usr/bin/env python3`) and executable permission (`chmod +x app.py`). The host runs `/workspace/mini-web-apps/budget/app.py` inside this conversation's sandbox. It does not execute files from another workspace. The LLM sends `[Open Budget Explorer](#mini-app?workspace=WORKSPACE_ID&app=budget)` using the workspace id supplied by Mini Web App Builder mode. The client resolves that link against the conversation's persisted workspace and receives a typed `kind: mini-web-app` record before opening the app.

The dedicated HTTPS app origin, enforced sign-in, and a gateway agent supporting operation protocol v4 are still required. If any runtime check fails, the app route is unavailable. The current installed agent image must be upgraded for this path; the mode alone does not upgrade it.
