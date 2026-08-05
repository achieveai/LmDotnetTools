// transcript-mirror.mjs — single-call Playwright E2E driver AND verifier for the WORKSPACE
// CONVERSATION TRANSCRIPT MIRROR (#251 / PR #252) and its two follow-up fixes on
// `wt3-transcript-followup`.
//
//   browser_run_code_unsafe({ filename: "<path>/transcript-mirror.mjs" })
//
// WHAT THIS PROVES
//   (a) a provider that ALWAYS attached           → `test-anthropic` + workspace-agent mode, turn 1
//   (c) a named background sub-agent              → turn 1 spawns `mirror-alpha` / `mirror-beta`
//   (b) a CLI/mock provider that DID NOT attach   → same thread switched to `codex-mock`, turn 2
//       ^^^ THIS IS THE P1 PROOF (458dbca1). Before P1 the CLI provider branches in Program.cs
//       returned before the single Attach call, so those agents mirrored nothing. Because turn 1
//       already created the file, the bug's signature is: file exists, contains MARKER1, and MARKER2
//       is ABSENT — a silent hole in the middle of a live transcript.
//   P2 (57bf4200, FIFO flush drain) is checked best-effort: no duplicate `uid`s, and MARKER1's row
//       precedes MARKER2's row. A drain that re-ordered or replayed a queued flush shows up here.
//
// HOW IT VERIFIES — read this before changing the assertions.
// The mirror writes into the SANDBOX WORKSPACE through the gateway, so the bytes are NOT on the
// browser side. Earlier drafts therefore punted the verdict to a host-filesystem PowerShell script.
// That is WRONG on this deployment: `samples/LmStreaming.Sample/.env` pins
// `SandboxGateway__BaseUrl=http://192.168.11.139:3000` with `AutoSpawn=false`, and 192.168.11.139 is
// a DIFFERENT machine (this host is 192.168.11.20; the route to .139 leaves via the Tailscale
// interface, and the two gateways report different `active_sessions`). The transcript lands on
// whichever machine the configured gateway runs on, so a host-path check on the wrong machine reads
// as "no file" — a FALSE P1 REGRESSED.
//
// So this script reads the transcript back through the app's own file browser
// (`FileBrowserController`, `[Route("api/conversations/{threadId}/files")]`), which goes through the
// SAME gateway the writer used and is therefore correct whether that gateway is local or remote.
// `disk-checks.ps1` remains useful only as a second opinion when the gateway is the local one.
//   • `files?path=…`          → listing (200 + `{state:"no_session_yet"}` before a sandbox exists)
//   • `files/download?path=…` → raw bytes. Use THIS, not `preview`: `FilePreviewPolicy` excludes
//     anything under a dot-directory outright, so `preview` returns `{previewable:false,
//     reason:"excluded"}` for `.conversations/*.jsonl` BY DESIGN. Not a bug — do not "fix" it.
//   • `[InboundS2SAuth]` is marker-gated (it enforces only when `X-S2S-Auth` / `X-Sbx-App-Id` is
//     present), so plain same-origin `fetch` from the page is allowed. All API calls run in-page.
//
// KNOWN INCONCLUSIVE PATH: the controller resolves a path one component at a time against the
// gateway's listing, so reaching `.conversations` needs the ROOT listing to include dot entries. The
// UI can navigate there (that is why the preview exclusion exists), but this script does not assume
// it — step `workspace root listing` records every root entry name, and if `.conversations` never
// resolves the verdict is INCONCLUSIVE_NOT_LISTABLE, never REGRESSED. Fall back to disk-checks.ps1.
//
// HARD PREREQUISITE: a LIVE SANDBOX GATEWAY. With no workspace binding every flush returns
// TranscriptFlushOutcome.Unavailable and writes nothing, silently — indistinguishable from the bug.
// The script fails fast at `workspace bound before send` rather than producing a false negative.
//
// Prompt: PromptExamples.md → "Sub-Agent Tabs (center-pane, colored)" → "Two named background
// sub-agents, text-only (transcript-mirror fixture)". Nested chains are built with JSON.stringify so
// the escaping is correct by construction — never hand-escape these.
async (page) => {
  // PORTS — there is NO launchSettings.json in samples/LmStreaming.Sample, so nothing overrides the
  // binding except `samples/LmStreaming.Sample/.env`, which Program.cs:66 loads via FindEnvFile.
  // FindEnvFile walks up from **AppContext.BaseDirectory** (the bin dir), NOT the shell cwd, so the
  // launch directory cannot change which .env wins. That file sets
  // ASPNETCORE_URLS=http://0.0.0.0:5055. `:5000` is the bare framework default you only get without
  // it; `:5050`/`:5098`/`:5273` are isolated instances. Probe rather than pin — put your origin
  // first to skip the probe.
  const CANDIDATES = [
    'http://localhost:5055',
    'http://localhost:5050',
    'http://localhost:5000',
    'http://localhost:5098',
    'http://localhost:5273',
    'http://localhost:5173',
  ];
  const WORKSPACE_NAME = 'transcript-e2e';
  const WORKSPACE_DIR = 'transcript-e2e'; // single segment — separators are stripped by the sanitizer
  const BOUND_PROVIDER = 'test-anthropic'; // the one mock Workspace Agent mode permits
  const CLI_PROVIDER = 'codex-mock'; // the only CLI-family provider needing no binary on PATH
  const TRANSCRIPT_DIR = '.conversations'; // ConversationTranscriptWriter.TranscriptDirectory
  const AGENTS_SUFFIX = '_agents'; // ConversationTranscriptWriter.AgentsDirectorySuffix
  const STAMP = String(Date.now());
  const TITLE = `Transcript Mirror E2E ${STAMP}`; // slugs to `transcript-mirror-e2e-<stamp>`
  // Every `.jsonl` under `.conversations/` is NOT this run's transcript — the directory accumulates
  // one per conversation, so on the SECOND run "the first .jsonl in the listing" is a previous run's
  // file, whose MARKER1 carries a previous stamp. That reads as FAILED_TURN1_NOT_MIRRORED against a
  // perfectly healthy mirror. Match this run's file by its own slug prefix instead.
  const EXPECTED_PREFIX = `transcript-mirror-e2e-${STAMP}-`;
  const isOurTranscript = (name) =>
    String(name).startsWith(EXPECTED_PREFIX) && String(name).endsWith('.jsonl');
  const MARKER1 = `MIRROR-TURN1-${STAMP}`;
  const MARKER2 = `MIRROR-TURN2-CLI-${STAMP}`;
  const ALPHA_TEXT = `mirror-alpha reporting ${MARKER1}`;
  const BETA_TEXT = `mirror-beta reporting ${MARKER1}`;
  // The flush is scheduled/debounced (TranscriptFlushScheduler), so the bytes trail the UI going
  // idle. These are generous ceilings on a poll, not sleeps — every wait exits on first success.
  const APPEAR_TIMEOUT_MS = 120000;
  const POLL_MS = 2000;

  const steps = [];
  const record = (name, pass, detail) => {
    steps.push({ name, pass, detail });
    return pass;
  };
  const tid = (id) => page.locator(`[data-testid="${id}"]`);
  const waitIdle = async () => {
    await tid('stop-button').waitFor({ state: 'hidden', timeout: 120000 });
    await tid('send-button').waitFor({ state: 'visible', timeout: 120000 });
  };
  const send = async (text) => {
    await tid('chat-input-textarea').fill(text);
    await tid('send-button').click();
  };
  const errorBanner = async () => {
    const n = await tid('error-banner').count();
    return n === 0 ? null : (await tid('error-banner').first().innerText().catch(() => '<unreadable>'));
  };

  let BASE = null;

  // Same-origin, in-page fetch — the interactive path [InboundS2SAuth] deliberately lets through.
  const api = (method, path, body) =>
    page.evaluate(
      async ({ method, path, body }) => {
        const init = { method, headers: { 'content-type': 'application/json' } };
        if (body !== undefined) init.body = JSON.stringify(body);
        const res = await fetch(`${location.origin}${path}`, init);
        const text = await res.text();
        let json = null;
        try {
          json = text ? JSON.parse(text) : null;
        } catch {
          /* non-JSON body — the raw text is kept for the failure detail */
        }
        return { status: res.status, ok: res.ok, json, text: text.slice(0, 600) };
      },
      { method, path, body }
    );

  // Raw workspace bytes (transcripts are JSONL; `preview` refuses dot-directories by design).
  const downloadWorkspaceFile = (threadId, wsPath) =>
    page.evaluate(
      async ({ threadId, wsPath }) => {
        const url = `${location.origin}/api/conversations/${encodeURIComponent(threadId)}/files/download?path=${encodeURIComponent(wsPath)}`;
        const res = await fetch(url);
        return { status: res.status, ok: res.ok, text: res.ok ? await res.text() : null };
      },
      { threadId, wsPath }
    );

  // Classifies a listing into the four states that mean different things here. `no_session_yet` is
  // NOT "empty": it means no sandbox has been established, so nothing could have been written.
  const listWorkspaceDir = async (threadId, wsPath) => {
    const res = await api('GET', `/api/conversations/${encodeURIComponent(threadId)}/files?path=${encodeURIComponent(wsPath)}`);
    if (res.status === 404) return { state: 'not-found', entries: [] };
    if (!res.ok) return { state: 'error', entries: [], status: res.status, body: res.text };
    if (res.json && res.json.state === 'no_session_yet') return { state: 'no-session', entries: [] };
    const entries = (res.json && res.json.entries) || [];
    return { state: 'listed', entries, moreCount: (res.json && res.json.moreCount) || 0 };
  };

  const pollUntil = async (fn, timeoutMs) => {
    const deadline = Date.now() + timeoutMs;
    let last = null;
    for (;;) {
      last = await fn();
      if (last && last.done) return last;
      if (Date.now() >= deadline) return last;
      await page.waitForTimeout(POLL_MS);
    }
  };

  // UI-idle is NOT run-idle. When a turn spawns BACKGROUND sub-agents, the parent's stop-button
  // hides as soon as the PARENT's own stream ends, but `agentPool.GetRunStateInfo(threadId)` stays
  // IsInProgress until the whole run drains. The mode/provider switch guards
  // (ConversationsController.cs:1263, :1380) read exactly that state, so switching on UI-idle earns
  // a 409 `mode_switch_while_streaming` — observed as AgentIsRunning=True, RunTaskCompleted=False,
  // IsStale=False, i.e. a genuinely live run, not a stale-state bug. Gate on the same signal the
  // guard reads rather than on a proxy for it.
  const waitRunIdle = (threadId, timeoutMs) =>
    pollUntil(async () => {
      const rs = await api('GET', `/api/conversations/${encodeURIComponent(threadId)}/run-state`);
      return { done: !(rs.json && rs.json.isInProgress), runState: rs.json };
    }, timeoutMs);

  const result = {
    pass: false,
    verdict: 'NOT_REACHED',
    failures: [],
    steps,
    base: null,
    workspace: null,
    conversation: null,
    transcript: null,
    markers: { turn1: MARKER1, turn2: MARKER2, alpha: ALPHA_TEXT, beta: BETA_TEXT },
    subAgents: [],
    diskChecks: null,
  };

  try {
    // ---- 0. Find the running instance -------------------------------------------------------
    for (const candidate of CANDIDATES) {
      const res = await page.request
        .get(`${candidate}/api/providers`, { timeout: 4000 })
        .catch(() => null);
      if (res && res.ok()) {
        BASE = candidate;
        break;
      }
    }
    if (!record('backend reachable', BASE !== null, { tried: CANDIDATES, base: BASE })) {
      result.failures.push('backend reachable');
      result.verdict = 'INCONCLUSIVE_NO_BACKEND';
      return result;
    }
    result.base = BASE;
    await page.goto(BASE);
    await tid('chat-input-textarea').waitFor({ timeout: 30000 });

    // ---- 1. Providers actually available on this host -------------------------------------
    // `/api/providers` returns an ENVELOPE `{providers:[…], default:"…"}`, not a bare array. Calling
    // .filter on the envelope throws, and the throw is swallowed by the outer catch as
    // INCONCLUSIVE_SCRIPT_ERROR — a script bug wearing the costume of an environment problem.
    const providers = await api('GET', '/api/providers');
    const providerList = Array.isArray(providers.json)
      ? providers.json
      : (providers.json && providers.json.providers) || [];
    const availableIds = providerList
      .filter((p) => p.isAvailable !== false && p.available !== false)
      .map((p) => p.id);
    const boundOk = availableIds.includes(BOUND_PROVIDER);
    const cliOk = availableIds.includes(CLI_PROVIDER);
    record('required providers available', boundOk && cliOk, {
      needs: [BOUND_PROVIDER, CLI_PROVIDER],
      boundOk,
      cliOk,
      availableIds,
      hint: cliOk ? undefined : 'codex-mock also requires MockProviderHostLifetime.IsRunning',
    });
    if (!boundOk) {
      result.failures.push('required providers available');
      result.verdict = 'INCONCLUSIVE_NO_PROVIDER';
      return result;
    }

    // ---- 2. Resolve-or-create the workspace BY NAME (a hardcoded id goes stale across data dirs)
    const list = await api('GET', '/api/workspaces');
    const workspaces = Array.isArray(list.json) ? list.json : (list.json && list.json.workspaces) || [];
    let workspace = workspaces.find(
      (w) => w.name === WORKSPACE_NAME || w.directoryRelPath === WORKSPACE_DIR
    );
    if (!workspace) {
      const created = await api('POST', '/api/workspaces', {
        name: WORKSPACE_NAME,
        directoryRelPath: WORKSPACE_DIR,
        marketplaces: [],
      });
      workspace = created.json;
      if (!record('workspace created', created.ok && !!(workspace && workspace.id), {
        status: created.status,
        body: created.text,
      })) {
        result.failures.push('workspace created');
        result.verdict = 'INCONCLUSIVE_NO_WORKSPACE';
        return result;
      }
    } else {
      record('workspace resolved (pre-existing)', true, {
        id: workspace.id,
        name: workspace.name,
        directoryRelPath: workspace.directoryRelPath,
      });
    }
    result.workspace = {
      id: workspace.id,
      name: workspace.name,
      // Report this: it names the directory the transcript will appear under, and it is the only
      // way the caller can tell WHICH workspace on WHICH gateway host was actually used.
      directoryRelPath: workspace.directoryRelPath ?? WORKSPACE_DIR,
      known: workspaces.map((w) => ({ id: w.id, name: w.name, dir: w.directoryRelPath })),
    };

    // ---- 3. Modes: find workspace-agent and a non-workspace fallback -----------------------
    // The route is `/api/chat-modes` (see ClientApp/src/api/chatModesApi.ts). `/api/modes` does NOT
    // 404 — it falls through to the SPA fallback and returns index.html, so JSON.parse fails, json is
    // null, and modeIds silently becomes []. A wrong route here reads as "this host has no modes".
    const modes = await api('GET', '/api/chat-modes');
    const modeList = Array.isArray(modes.json) ? modes.json : (modes.json && modes.json.modes) || [];
    const modeIds = modeList.map((m) => m.id);
    const wsMode = modeIds.includes('workspace-agent') ? 'workspace-agent' : null;
    const plainMode = modeIds.includes('default')
      ? 'default'
      : modeIds.find((m) => m !== 'workspace-agent' && m !== 'workflow-author');
    if (!record('modes resolved', !!wsMode && !!plainMode, { wsMode, plainMode, modeIds })) {
      result.failures.push('modes resolved');
      result.verdict = 'INCONCLUSIVE_NO_MODE';
      return result;
    }

    // ---- 4. Provision the workspace-bound conversation (race-free; no UI clicking) ---------
    const prov = await api('POST', '/api/conversations', {
      workspaceId: result.workspace.id,
      providerId: BOUND_PROVIDER,
      modeId: wsMode,
    });
    const threadId = prov.json && prov.json.threadId;
    if (!record('conversation provisioned (workspace-agent)', prov.ok && !!threadId, {
      status: prov.status,
      body: prov.text,
      hint: prov.status === 503 ? 'provider_unavailable — check the mock host / gateway' : undefined,
    })) {
      result.failures.push('conversation provisioned (workspace-agent)');
      result.verdict = 'INCONCLUSIVE_NO_THREAD';
      return result;
    }
    await api('PUT', `/api/conversations/${threadId}/metadata`, { title: TITLE });
    result.conversation = { threadId, title: TITLE, providerAtTurn1: BOUND_PROVIDER, modeAtTurn1: wsMode };

    // ---- 5. The workspace really is bound BEFORE we send anything --------------------------
    // A missing workspace property means every flush returns Unavailable and NOTHING is written —
    // which looks exactly like the P1 bug. Refuse to continue rather than emit a false negative.
    const convs = await api('GET', '/api/conversations');
    const summary = (convs.json || []).find((c) => c.threadId === threadId);
    if (!record('workspace bound before send', !!(summary && summary.workspace), {
      workspace: summary && summary.workspace,
      provider: summary && summary.provider,
      mode: summary && summary.mode,
      why: 'no workspace ⇒ TranscriptFlushOutcome.Unavailable ⇒ no file, silently',
    })) {
      result.failures.push('workspace bound before send');
      result.verdict = 'INCONCLUSIVE_UNBOUND';
      return result;
    }

    // ---- 5b. BASELINE, recorded through the same reader that will later prove the bytes -----
    // Taking the baseline through the gateway (rather than a host path) is what makes the later
    // "the file appeared" claim mean something: same reader, same session, same machine.
    const baseRoot = await listWorkspaceDir(threadId, '');
    record('baseline: workspace root listing', true, {
      state: baseRoot.state,
      entries: baseRoot.entries.map((e) => `${e.name}${e.type === 'directory' ? '/' : ''}`),
      dotEntriesVisible: baseRoot.entries.some((e) => String(e.name).startsWith('.')),
      why: 'if dot entries never appear at root, `.conversations` cannot be resolved by this API',
    });
    const baseTranscriptDir = await listWorkspaceDir(threadId, TRANSCRIPT_DIR);
    const baselineClean =
      baseTranscriptDir.state === 'not-found' ||
      baseTranscriptDir.state === 'no-session' ||
      (baseTranscriptDir.state === 'listed' &&
        !baseTranscriptDir.entries.some((e) => isOurTranscript(e.name)));
    record(`baseline: no ${TRANSCRIPT_DIR} transcript yet`, baselineClean, {
      state: baseTranscriptDir.state,
      entries: baseTranscriptDir.entries.map((e) => e.name),
      meaning:
        baseTranscriptDir.state === 'no-session'
          ? 'no sandbox established yet — nothing could have been written; the cleanest baseline'
          : baseTranscriptDir.state === 'not-found'
            ? 'directory does not exist'
            : 'directory exists but holds no transcript',
    });

    // ---- 6. Turn 1 on the attaching provider, spawning two NAMED background sub-agents -----
    const inner = (id, text) =>
      `<|instruction_start|>${JSON.stringify({ instruction_chain: [{ id, messages: [{ text }] }] })}<|instruction_end|>`;
    const parent = {
      instruction_chain: [
        {
          id: 'spawn-mirror-pair',
          id_message: 'Spawn two named background sub-agents',
          reasoning: { length: 30 }, // reasoning must appear RAW in the transcript — checked below
          messages: [
            {
              tool_call: [
                {
                  name: 'Agent',
                  args: {
                    subagent_type: 'researcher',
                    name: 'mirror-alpha',
                    run_in_background: true,
                    prompt: inner('ma1', ALPHA_TEXT),
                  },
                },
                {
                  name: 'Agent',
                  args: {
                    subagent_type: 'general-purpose',
                    name: 'mirror-beta',
                    run_in_background: true,
                    prompt: inner('mb1', BETA_TEXT),
                  },
                },
              ],
            },
          ],
        },
        {
          id: 'parent-done',
          messages: [{ text: `Spawned mirror-alpha and mirror-beta. ${MARKER1}` }],
        },
      ],
    };
    const PROMPT1 = `${MARKER1}\n<|instruction_start|>${JSON.stringify(parent)}<|instruction_end|>`;

    await page.goto(`${BASE}/?threadId=${threadId}`);
    await tid('chat-input-textarea').waitFor({ timeout: 30000 });
    const notFound = await tid('conversation-not-found').count();
    if (!record('deep link opened the provisioned thread', notFound === 0, { threadId, notFound })) {
      result.failures.push('deep link opened the provisioned thread');
      result.verdict = 'INCONCLUSIVE_DEAD_THREAD';
      return result;
    }

    await send(PROMPT1);
    await waitIdle();
    const err1 = await errorBanner();
    record(`turn 1 ran on ${BOUND_PROVIDER} without error`, err1 === null, {
      errorBanner: err1,
      why: 'an error here usually means the sandbox gateway is down — the read-back would be meaningless',
    });
    if (err1 !== null) {
      result.failures.push(`turn 1 ran on ${BOUND_PROVIDER} without error`);
      result.verdict = 'INCONCLUSIVE_TURN1_ERROR';
      return result;
    }

    // ---- 7. The two named sub-agents exist (⇒ an `_agents/` child file is expected) ---------
    let rows = [];
    for (let i = 0; i < 20; i++) {
      const sub = await api('GET', `/api/conversations/${threadId}/subagents`);
      rows = Array.isArray(sub.json) ? sub.json : [];
      if (rows.length >= 2) break;
      await page.waitForTimeout(1500);
    }
    result.subAgents = rows.map((r) => ({
      name: r.name ?? r.agentName ?? null,
      agentId: r.agentId ?? r.id ?? null,
      threadId: r.threadId ?? null,
      status: r.status ?? null,
    }));
    record('two named background sub-agents spawned', rows.length >= 2, {
      count: rows.length,
      subAgents: result.subAgents,
      expectedNames: ['mirror-alpha', 'mirror-beta'],
    });

    // ---- 7b. THE POSITIVE ASSERTION: real bytes, read back through the gateway --------------
    // Absence of errors is not evidence. This is: the file exists and contains MARKER1.
    const appeared = await pollUntil(async () => {
      const listing = await listWorkspaceDir(threadId, TRANSCRIPT_DIR);
      const file = listing.entries.find((e) => isOurTranscript(e.name));
      return { done: !!file, listing, file };
    }, APPEAR_TIMEOUT_MS);

    if (!appeared || !appeared.file) {
      const listable = appeared && appeared.listing.state !== 'error';
      record(`transcript file created under ${TRANSCRIPT_DIR}`, false, {
        state: appeared && appeared.listing.state,
        entries: appeared ? appeared.listing.entries.map((e) => e.name) : [],
        expectedPrefix: EXPECTED_PREFIX,
        note: listable
          ? `directory readable but nothing matching ${EXPECTED_PREFIX} — the mirror wrote nothing on the ATTACHING provider`
          : 'directory not readable through this API — fall back to disk-checks.ps1',
      });
      result.failures.push(`transcript file created under ${TRANSCRIPT_DIR}`);
      result.verdict = listable ? 'FAILED_NO_TRANSCRIPT_AT_ALL' : 'INCONCLUSIVE_NOT_LISTABLE';
      return result;
    }

    const fileName = appeared.file.name;
    const leaf = fileName.replace(/\.jsonl$/, '');
    const mainPath = `${TRANSCRIPT_DIR}/${fileName}`;
    record(`transcript file created under ${TRANSCRIPT_DIR}`, true, {
      fileName,
      size: appeared.file.size,
      expectedShape: `slug(title)-shortId(threadId).jsonl  (slug ⇒ transcript-mirror-e2e-${STAMP})`,
    });
    result.transcript = { dir: TRANSCRIPT_DIR, fileName, leaf, path: mainPath };

    const turn1Bytes = await pollUntil(async () => {
      const dl = await downloadWorkspaceFile(threadId, mainPath);
      return { done: !!(dl.ok && dl.text && dl.text.includes(MARKER1)), dl };
    }, APPEAR_TIMEOUT_MS);
    const turn1Text = (turn1Bytes && turn1Bytes.dl.text) || '';
    if (!record('turn 1 bytes are in the transcript (MARKER1)', turn1Text.includes(MARKER1), {
      status: turn1Bytes && turn1Bytes.dl.status,
      bytes: turn1Text.length,
      lines: turn1Text ? turn1Text.split('\n').filter((l) => l.trim()).length : 0,
      // NO reasoning check here. It was tried and it lies twice over: this snapshot is taken before
      // reasoning has flushed, and a /"reasoning"/ scan matches the User message's own message_json,
      // which echoes the prompt's `"reasoning":{"length":30}` back verbatim. RAW fidelity is asserted
      // against the FINAL transcript off the message_type discriminator instead — see step 12.
    })) {
      result.failures.push('turn 1 bytes are in the transcript (MARKER1)');
      result.verdict = 'FAILED_TURN1_NOT_MIRRORED';
      return result;
    }

    // Sub-agent fan-out — case (c). Recorded, not fatal: P1 is about the parent attach.
    const agentsDir = `${TRANSCRIPT_DIR}/${leaf}${AGENTS_SUFFIX}`;
    // POLL for BOTH children. Each sub-agent flushes on its own schedule, so a single-shot listing
    // taken right after the parent's flush reliably catches only the first — a script race that
    // reads as "the second sub-agent was never mirrored". Observed exactly once: alpha present /
    // beta absent at list time, beta on disk (2431 bytes) moments later. This directory is
    // per-conversation, so a bare `.jsonl` filter is safe here (unlike `.conversations/` itself).
    const agentsPoll = await pollUntil(async () => {
      const listing = await listWorkspaceDir(threadId, agentsDir);
      const files = listing.entries.filter((e) => String(e.name).endsWith('.jsonl'));
      return { done: files.length >= 2, listing, files };
    }, APPEAR_TIMEOUT_MS);
    const agentsListing = agentsPoll.listing;
    const agentFiles = agentsPoll.files;
    const agentTexts = {};
    let agentsBlob = '';
    for (const f of agentFiles) {
      const dl = await downloadWorkspaceFile(threadId, `${agentsDir}/${f.name}`);
      const text = (dl.ok && dl.text) || '';
      agentTexts[f.name] = text.length;
      agentsBlob += `${text}\n`;
    }
    record('sub-agent transcripts mirrored', agentFiles.length >= 2, {
      dir: agentsDir,
      state: agentsListing.state,
      files: agentFiles.map((f) => f.name),
      byteCounts: agentTexts,
      alphaPresent: agentsBlob.includes(ALPHA_TEXT),
      betaPresent: agentsBlob.includes(BETA_TEXT),
      expectedLeafShape: 'slug(agentName)-shortId(agentId).jsonl ⇒ mirror-alpha-… / mirror-beta-…',
    });

    // ---- 8. THE P1 SETUP: leave workspace mode, switch to the CLI-family provider -----------
    // Order matters: workspace-agent mode REJECTS codex-mock (Program.cs:822-856), so the mode must
    // drop first. The established sandbox binding survives both switches — PublishBindingIfStaged
    // only ever publishes, and ClearEstablishedBinding fires only on agent REMOVAL — so the
    // codex-mock agent's flush WOULD write, iff P1 attached the mirror to it.
    if (!cliOk) {
      record(`SKIPPED: switch to ${CLI_PROVIDER}`, false, {
        why: `${CLI_PROVIDER} is not available on this host — the P1 case cannot be driven`,
        availableIds,
      });
      result.failures.push(`SKIPPED: switch to ${CLI_PROVIDER}`);
      result.verdict = 'INCONCLUSIVE_NO_CLI_PROVIDER';
      return result;
    }
    const runIdle = await waitRunIdle(threadId, APPEAR_TIMEOUT_MS);
    if (!record('run drained server-side before switching', !!(runIdle && runIdle.done), {
      runState: runIdle && runIdle.runState,
      why: 'the switch guards read agentPool.GetRunStateInfo; UI-idle is not run-idle when the turn spawned background sub-agents',
    })) {
      result.failures.push('run drained server-side before switching');
      result.verdict = 'INCONCLUSIVE_RUN_NEVER_DRAINED';
      return result;
    }
    const modeSwitch = await api('POST', `/api/conversations/${threadId}/mode`, { modeId: plainMode });
    record('mode switched out of workspace-agent', modeSwitch.ok, {
      to: plainMode,
      status: modeSwitch.status,
      body: modeSwitch.text,
    });
    const provSwitch = await api('POST', `/api/conversations/${threadId}/provider`, {
      providerId: CLI_PROVIDER,
    });
    if (!record(`provider switched to ${CLI_PROVIDER}`, provSwitch.ok, {
      status: provSwitch.status,
      body: provSwitch.text,
    })) {
      result.failures.push(`provider switched to ${CLI_PROVIDER}`);
      result.verdict = 'INCONCLUSIVE_NO_SWITCH';
      return result;
    }

    // Workspace metadata must be untouched by the two switches, or turn 2 proves nothing.
    const convs2 = await api('GET', '/api/conversations');
    const summary2 = (convs2.json || []).find((c) => c.threadId === threadId);
    if (!record('workspace binding survived the switches', !!(summary2 && summary2.workspace), {
      workspace: summary2 && summary2.workspace,
      provider: summary2 && summary2.provider,
      mode: summary2 && summary2.mode,
    })) {
      result.failures.push('workspace binding survived the switches');
      result.verdict = 'INCONCLUSIVE_BINDING_LOST';
      return result;
    }
    result.conversation.providerAtTurn2 = CLI_PROVIDER;
    result.conversation.modeAtTurn2 = plainMode;

    // ---- 9. Turn 2 on the CLI-family provider — the byte that P1 is about ------------------
    // Plain text: codex-mock does not interpret the instruction-chain format, and the assertion
    // rides on the USER message (which the mirror records) rather than any scripted reply.
    await page.reload();
    await tid('chat-input-textarea').waitFor({ timeout: 30000 });
    const beforeUser = await tid('user-message-group').count();
    await send(`${MARKER2} — please acknowledge.`);
    await waitIdle();
    // Drain server-side too: the mirror flushes at a TURN BOUNDARY, so reading the transcript while
    // the run is still in progress can miss MARKER2 for reasons that have nothing to do with P1.
    await waitRunIdle(threadId, APPEAR_TIMEOUT_MS);
    const afterUser = await tid('user-message-group').count();
    const afterAssistant = await tid('assistant-message-group').count();
    const err2 = await errorBanner();
    record(`turn 2 ran on ${CLI_PROVIDER}`, err2 === null && afterUser > beforeUser, {
      errorBanner: err2,
      userGroups: { before: beforeUser, after: afterUser },
      assistantGroups: afterAssistant,
      note: 'CLI mocks may complete silently — the user turn landing is what matters',
    });
    if (err2 !== null || afterUser <= beforeUser) {
      result.failures.push(`turn 2 ran on ${CLI_PROVIDER}`);
      result.verdict = 'INCONCLUSIVE_TURN2_DID_NOT_RUN';
      return result;
    }

    // ---- 10. THE P1 VERDICT: MARKER2 must join MARKER1 in the SAME file --------------------
    const turn2Bytes = await pollUntil(async () => {
      const dl = await downloadWorkspaceFile(threadId, mainPath);
      return { done: !!(dl.ok && dl.text && dl.text.includes(MARKER2)), dl };
    }, APPEAR_TIMEOUT_MS);
    const finalText = (turn2Bytes && turn2Bytes.dl.text) || '';
    const hasM1 = finalText.includes(MARKER1);
    const hasM2 = finalText.includes(MARKER2);
    const p1 = record('P1 (458dbca1): the CLI-provider turn was mirrored too (MARKER2)', hasM2, {
      marker1Present: hasM1,
      marker2Present: hasM2,
      bytes: finalText.length,
      bytesAfterTurn1: turn1Text.length,
      signature: hasM1 && !hasM2 ? 'MARKER1 without MARKER2 = the exact P1 bug: a silent hole mid-transcript' : undefined,
    });
    if (!p1) result.failures.push('P1 (458dbca1): the CLI-provider turn was mirrored too (MARKER2)');

    // ---- 11. P2 (57bf4200) best-effort: the FIFO drain neither duplicated nor re-ordered ----
    const lines = finalText.split('\n').filter((l) => l.trim());
    const parsed = [];
    let unparsable = 0;
    for (const line of lines) {
      try {
        parsed.push(JSON.parse(line));
      } catch {
        unparsable++;
      }
    }
    const uids = parsed.map((r) => r && r.uid).filter(Boolean);
    const dupUids = uids.filter((u, i) => uids.indexOf(u) !== i);
    const idx1 = lines.findIndex((l) => l.includes(MARKER1));
    const idx2 = lines.findIndex((l) => l.includes(MARKER2));
    record('P2 (57bf4200): drain kept the transcript append-only and in order', dupUids.length === 0 && unparsable === 0 && (idx2 === -1 || idx1 < idx2), {
      lines: lines.length,
      parsed: parsed.length,
      unparsableLines: unparsable,
      duplicateUids: dupUids.slice(0, 5),
      marker1LineIndex: idx1,
      marker2LineIndex: idx2,
      why: 'a mis-drained queue shows up as a repeated uid, a torn line, or turn 2 landing before turn 1',
    });

    // ---- 12. RAW fidelity: reasoning survived the mirror --------------------------------------
    // The locked decision is FULL/RAW content fidelity INCLUDING reasoning, so a transcript that
    // silently dropped thinking would satisfy every check above and still be wrong.
    // Count `message_type`, NOT a substring. A /"reasoning"/ scan over the raw text reports ~49 hits
    // on a healthy run and would report them on a BROKEN one too, because the User row's
    // `message_json` quotes the prompt's own `"reasoning":{"length":30}`. The discriminator is the
    // only field the mock cannot forge from prompt text.
    const byType = {};
    for (const r of parsed) {
      const t = (r && r.message_type) || '(none)';
      byType[t] = (byType[t] || 0) + 1;
    }
    const reasoningRows = Object.entries(byType)
      .filter(([t]) => t.startsWith('Reasoning'))
      .reduce((n, [, c]) => n + c, 0);
    record('RAW fidelity: reasoning rows survived the mirror', reasoningRows > 0, {
      reasoningRows,
      messageTypes: byType,
      why: 'reasoning is part of the locked full-fidelity contract; counted by message_type because the prompt echoes the word "reasoning" into its own message_json',
    });
    if (reasoningRows === 0) result.failures.push('RAW fidelity: reasoning rows survived the mirror');

    result.verdict = hasM2 ? 'P1_HOLDS' : hasM1 ? 'P1_REGRESSED' : 'FAILED_NOTHING_MIRRORED';

    // ---- 13. Optional second opinion (LOCAL gateway only) ----------------------------------
    result.diskChecks = {
      how: 'run disk-checks.ps1 with -ThreadId / -Title / -Marker1 / -Marker2 / -WorkspaceDir',
      onlyValidWhen:
        'SandboxGateway:BaseUrl points at a gateway on THIS host. The .env pins http://192.168.11.139:3000 ' +
        '(a different machine, reached over Tailscale), in which case the bytes are on THAT host and this ' +
        'script\'s in-band read-back above is the authoritative check.',
      overrideForALocalRun:
        'dotnet run --project samples/LmStreaming.Sample -- --SandboxGateway:BaseUrl=http://127.0.0.1:3000 ' +
        '--SandboxGateway:WorkspaceBasePath=B:\\sandbox-workspaces\\workspaces   ' +
        '(command-line args outrank the .env; shell env vars do NOT — dotenv.net overwrites existing vars)',
      threadId,
      title: TITLE,
      workspaceDir: result.workspace.directoryRelPath,
      mainFile: mainPath,
      agentsDir,
    };
  } catch (e) {
    record('unexpected error', false, { message: String(e && e.message ? e.message : e) });
    result.failures.push('unexpected error');
    if (result.verdict === 'NOT_REACHED') result.verdict = 'INCONCLUSIVE_SCRIPT_ERROR';
  }

  for (const s of steps) {
    if (!s.pass && !result.failures.includes(s.name)) result.failures.push(s.name);
  }
  result.pass = result.failures.length === 0;
  return result
}
