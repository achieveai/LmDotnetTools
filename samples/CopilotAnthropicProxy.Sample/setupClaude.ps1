# ── Launch Claude Code against the local Copilot↔Anthropic proxy ──────────
# Ships alongside the proxy (copied to the build/publish output), so it finds
# the executable and the MCP config relative to itself — no absolute paths.
#
# Starts the proxy only if its port is free; otherwise assumes one is already
# running and just launches Claude.

Set-StrictMode -Version Latest

# The proxy reads COPILOT_ANTHROPIC_PORT and falls back to 8787 (Program.cs).
# Mirror that here so the port check, the base URL, and the MCP config agree.
$defaultPort = 8787
$port = $defaultPort
if ($env:COPILOT_ANTHROPIC_PORT) {
    $parsed = 0
    if ([int]::TryParse($env:COPILOT_ANTHROPIC_PORT, [ref]$parsed) -and $parsed -gt 0) {
        $port = $parsed
    }
    else {
        Write-Warning "COPILOT_ANTHROPIC_PORT='$env:COPILOT_ANTHROPIC_PORT' is not a valid port; using $defaultPort."
    }
}

function Test-ProxyListening {
    param([int]$Port)
    # The proxy binds BOTH loopback families, so a listener on either counts.
    [bool](Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue)
}

# ── Claude Code telemetry / privacy opt-outs ──────────────────────────────
# These control the CLAUDE CODE CLI's own outbound telemetry — separate from
# anything in this repo. Sets the current session ($env:) only — the proxy and
# `claude` below inherit it, but new terminals will not.

$claudeTelemetryVars = @{
    # Opts out of Statsig-based product analytics (usage patterns, feature flags)
    "DISABLE_TELEMETRY"               = "1"

    # Opts out of Sentry error/crash reporting
    "DISABLE_ERROR_REPORTING"         = "1"

    # Disables the /bug command, which otherwise sends conversation transcripts to Anthropic
    "DISABLE_BUG_COMMAND"             = "1"

    # Disables non-essential model calls (e.g. generating flavor/status text)
    "DISABLE_NON_ESSENTIAL_MODEL_CALLS" = "1"

    # Disables the auto-updater's background network calls (update checks/downloads)
    "DISABLE_AUTOUPDATER"             = "1"

    # OpenTelemetry metrics/logs export is opt-IN (default off); set explicitly to 0
    "CLAUDE_CODE_ENABLE_TELEMETRY"    = "0"

    # Point Claude at the proxy. The key is a placeholder: the proxy authenticates
    # to Copilot itself and ignores whatever the client sends.
    "ANTHROPIC_API_KEY"               = "sk-dummy"
    "ANTHROPIC_BASE_URL"              = "http://127.0.0.1:$port"
}

foreach ($kv in $claudeTelemetryVars.GetEnumerator()) {
    # Current session
    Set-Item -Path "Env:$($kv.Key)" -Value $kv.Value
}

# Confirm (session scope — that's what the loop above sets)
$claudeTelemetryVars.Keys | ForEach-Object {
    Write-Host "$_ = $([System.Environment]::GetEnvironmentVariable($_, 'Process'))"
}

# ── Start the proxy only if nothing is already on the port ────────────────
if (Test-ProxyListening -Port $port) {
    Write-Host "Port $port is already in use - assuming the proxy is running; not starting another."
}
else {
    $exeName = "CopilotAnthropicProxy.Sample.exe"
    # Published layout puts the exe next to this script; dev layout leaves the
    # script in the project directory with the exe under bin\<config>\<tfm>.
    $proxyExe =
        @(
            (Join-Path $PSScriptRoot $exeName),
            (Join-Path $PSScriptRoot "bin\Debug\net9.0\$exeName"),
            (Join-Path $PSScriptRoot "bin\Release\net9.0\$exeName")
        ) | Where-Object { Test-Path $_ } | Select-Object -First 1

    if (-not $proxyExe) {
        throw "Could not find $exeName next to this script or under bin\{Debug,Release}\net9.0. Build or publish the proxy first."
    }

    Write-Host "Starting proxy: $proxyExe"
    Start-Process -FilePath $proxyExe -WorkingDirectory (Split-Path -Parent $proxyExe)

    # Claude connects to the MCP server during startup, so don't race the proxy.
    $deadline = (Get-Date).AddSeconds(20)
    while (-not (Test-ProxyListening -Port $port) -and (Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 250
    }
    if (Test-ProxyListening -Port $port) {
        Write-Host "Proxy is listening on port $port."
    }
    else {
        Write-Warning "Proxy did not start listening on port $port within 20s; the 'web' MCP server may fail to connect."
    }
}

# ── Route web search through the proxy's MCP endpoint ─────────────────────
# Copilot's MCP server exposes `web_search`, but it is NOT in the default
# toolsets — the X-MCP-Tools allowlist is what surfaces it (and nothing else).
# Copilot has NO `web_fetch` (asking for it returns
# "unknown tools specified in WithTools: web_fetch"), so Claude's built-in
# WebFetch stays enabled; only WebSearch is disabled.
#
# A file rather than inline JSON: PowerShell mangles embedded double quotes
# when handing arguments to native commands.
$webMcpConfig = Join-Path $PSScriptRoot "claude-web-mcp.json"
if (-not (Test-Path $webMcpConfig)) {
    throw "Missing claude-web-mcp.json next to this script."
}
if ($port -ne $defaultPort) {
    # Ship a port-corrected copy rather than writing back into the install dir,
    # which may be read-only.
    $cfg = Get-Content $webMcpConfig -Raw | ConvertFrom-Json
    $cfg.mcpServers.web.url = "http://127.0.0.1:$port/mcp"
    $webMcpConfig = Join-Path $env:TEMP "claude-web-mcp.json"
    $cfg | ConvertTo-Json -Depth 10 | Set-Content -Path $webMcpConfig -Encoding utf8
}

# --disallowedTools still applies under bypassPermissions: rules evaluate
# deny -> ask -> allow, and bypassPermissions only skips the prompts.
claude --permission-mode bypassPermissions `
       --disallowedTools WebSearch `
       --mcp-config $webMcpConfig
