// workspace-agent-egress-auth-test.mjs — single-call diagnostic: does a REAL sandbox Bash tool call,
// issued from "Workspace Agent" mode with the "test" (Mock) provider, actually reach the sandbox
// egress proxy? Run with:
//
//   browser_run_code_unsafe({ filename: "samples/LmStreaming.Sample/playwright-scripts/workspace-agent-egress-auth-test.mjs" })
//
// This is a diagnostic script (not a CI regression — depends on a live sandbox gateway). It PROVISIONS
// the conversation headlessly via POST /api/conversations (mirrors ConversationDaemon.Sample and
// DeepLinkHandoffResumeTests.cs) and deep-links into it via ?threadId=, instead of clicking "+ New
// Chat" — the UI's "+ New Chat" button has a live race where the Send action can silently bind to
// whichever conversation/provider was PREVIOUSLY active instead of the fresh one (confirmed twice: it
// misrouted an instruction-chain prompt into a real, unrelated conversation under a real Copilot
// provider). Provisioning + a ?threadId= deep link is the app's own documented race-free mechanism for
// landing on an exact, explicitly-bound conversation (ChatLayout.vue reads ?threadId= on mount and
// restores that conversation's bound provider/mode/workspace deterministically).
//
// Selects the "LmDotnetTools" workspace, the "test" (Test (Mock)) provider, and "Workspace Agent" mode,
// then sends an instruction-chain prompt whose tool_call targets the REAL "Bash" tool (Workspace Agent
// mode wires middleware providers — including "test"/"test-anthropic" — to the sandbox gateway's actual
// tool set, per Program.cs's exclusion list which only blocks codex/claude/*-mock CLI providers).
//
// The Bash command deliberately does NOT depend on the egress-auth skill's scripts/ subdirectory
// (confirmed missing from the sandbox mount in a prior run — a gateway/mount config gap, not what
// we're testing here). Instead it uses `env` + `curl`, which are near-guaranteed present in the
// sandbox image, to directly probe the thing we actually care about:
//   1. `env | grep -i -E 'proxy|ssl_cert' | sed 's/=.*/=<redacted>/'` — is the sandbox wired to route
//      through an egress-forward proxy at all (HTTP_PROXY/HTTPS_PROXY set)? Necessary precondition for
//      egress auth to apply. The values are REDACTED before printing — HTTP_PROXY/HTTPS_PROXY can carry
//      embedded proxy credentials, so we emit only the variable NAMES (whether they are set), never the
//      raw values, into tool output.
//   2. `curl -sS -w '\nHTTP_STATUS:%{http_code}\n' https://api.github.com/user --max-time 30` — we send
//      NO Authorization header ourselves. HTTP_STATUS:200 + real GitHub user JSON means the proxy
//      transparently authenticated the request (egress auth genuinely working). HTTP_STATUS:401 + a
//      real GitHub "Requires authentication" body means network egress works but token injection is
//      NOT happening. A connect/resolve/timeout failure means network is locked down or misconfigured.
async (page) => {
  const BASE = 'http://127.0.0.1:5050';
  const WORKSPACE_ID = '192a3465-67a1-4945-9323-44c2168aeb2b'; // "LmDotnetTools"
  const PROVIDER_ID = 'test'; // "Test (Mock)"
  const MODE_ID = 'workspace-agent'; // "Workspace Agent"

  // `sed 's/=.*/=<redacted>/'` collapses each matched `NAME=value` line to `NAME=<redacted>` so the
  // presence of HTTP_PROXY/HTTPS_PROXY/SSL_CERT* is diagnosable WITHOUT leaking any (possibly
  // credential-bearing) proxy value into the captured tool output.
  const bashCommand =
    "env | grep -i -E 'proxy|ssl_cert' | sed -E 's/=.*/=<redacted>/' ; echo '---' ; curl -sS -w '\\nHTTP_STATUS:%{http_code}\\n' https://api.github.com/user --max-time 30";

  const instructionChain = {
    instruction_chain: [
      {
        id: 'egress-auth-bash',
        id_message: 'Testing egress auth via real sandbox Bash tool',
        messages: [{ tool_call: [{ name: 'Bash', args: { command: bashCommand } }] }],
      },
    ],
  };
  const PROMPT = `<|instruction_start|>${JSON.stringify(instructionChain)}<|instruction_end|>`;

  const steps = [];
  // Steps recorded here are informational only — known secondary issues (e.g. a client-side
  // workspace-selector load-order race, root-caused separately) that must NOT block sending the
  // egress-auth probe. They still show up in `steps` for visibility but are excluded from the
  // pre-send abort gate and from the final pass/fail rollup.
  const nonBlocking = new Set(['workspace-bound (LmDotnetTools)']);
  const record = (name, pass, detail) => steps.push({ name, pass, detail });
  const tid = (id) => page.locator(`[data-testid="${id}"]`);

  // The deep-link mount's restoreBindingsFromConversation(threadId) races against the app's own
  // on-mount GET /api/conversations list refresh — if that hasn't resolved to include our
  // just-provisioned thread yet, the restore no-ops and selectors show hardcoded app defaults
  // ("Default"/"Anthropic"/"General Assistant") rather than what we provisioned. This is a real but
  // transient client hydration race (confirmed: same script, same thread shape, 3 different runs
  // landed 3 different partial-vs-full-bind outcomes). Poll for up to `timeoutMs` for the label to
  // settle to the expected value instead of reading it once immediately — a persistent mismatch
  // still fails after the deadline, exactly like `waitFor` already does for chat-input-textarea.
  async function waitForLabelMatch(getLabel, regex, timeoutMs = 8000, intervalMs = 250) {
    const deadline = Date.now() + timeoutMs;
    let last = null;
    while (Date.now() < deadline) {
      last = await getLabel();
      if (regex.test(last ?? '')) return last;
      await new Promise((r) => setTimeout(r, intervalMs));
    }
    return last;
  }

  try {
    await page.goto(BASE);
    await tid('chat-input-textarea').waitFor({ timeout: 20000 });

    // Provision a fresh, explicitly-bound conversation headlessly — no "+ New Chat" click, no race.
    const provisioned = await page.evaluate(
      async ({ workspaceId, providerId, modeId }) => {
        const res = await fetch(`${location.origin}/api/conversations`, {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ workspaceId, providerId, modeId }),
        });
        if (!res.ok) throw new Error(`provision failed: ${res.status} ${await res.text()}`);
        return res.json();
      },
      { workspaceId: WORKSPACE_ID, providerId: PROVIDER_ID, modeId: MODE_ID }
    );
    const threadId = provisioned && provisioned.threadId;
    record('provisioned-thread', !!threadId, provisioned);

    // Deep-link straight into it. ChatLayout.vue reads ?threadId= on mount, finds this exact
    // conversation, and restores its bound provider/mode/workspace — deterministic, not a live click.
    await page.goto(`${BASE}/?threadId=${encodeURIComponent(threadId)}`);
    await tid('chat-input-textarea').waitFor({ timeout: 20000 });

    const notFoundCount = await tid('conversation-not-found').count();
    record('deep-link-resolved', notFoundCount === 0, { notFoundCount });

    // WorkspaceSelector.vue renders one of two mutually-exclusive variants: an editable
    // `workspace-selector-button` (pre-lock) or a static `workspace-locked-badge` (once the
    // conversation's metadata already carries a workspace — which Provision sets immediately, so a
    // freshly-provisioned+deep-linked thread can render either variant depending on load timing).
    // Poll both (via `.count()` first so a missing one never hangs on a bare `.textContent()` wait)
    // until one matches, tolerating the mount-vs-list-refresh hydration race described above.
    const getWsLabel = async () => {
      const badgeCount = await tid('workspace-locked-badge').count();
      if (badgeCount > 0) return tid('workspace-locked-badge').textContent();
      const buttonCount = await tid('workspace-selector-button').count();
      return buttonCount > 0 ? tid('workspace-selector-button').textContent() : null;
    };
    const wsLabel = await waitForLabelMatch(getWsLabel, /LmDotnetTools/);
    record('workspace-bound (LmDotnetTools)', /LmDotnetTools/.test(wsLabel ?? ''), wsLabel);

    const providerLabel = await waitForLabelMatch(
      () => tid('provider-selector-button').textContent(),
      /Test/
    );
    record('provider-bound (Test Mock)', /Test/.test(providerLabel ?? ''), providerLabel);

    const modeLabel = await waitForLabelMatch(
      () => tid('mode-selector-button').textContent(),
      /Workspace Agent/
    );
    record('mode-bound (Workspace Agent)', /Workspace Agent/.test(modeLabel ?? ''), modeLabel);

    // Safety check BEFORE sending: this must be a genuinely empty thread we just provisioned.
    const preMessages = await page.evaluate(async (id) => {
      const r = await fetch(`${location.origin}/api/conversations/${encodeURIComponent(id)}/messages`);
      return r.ok ? r.json() : null;
    }, threadId);
    record('thread-empty-before-send', Array.isArray(preMessages) && preMessages.length === 0, {
      count: Array.isArray(preMessages) ? preMessages.length : preMessages,
    });

    // Refuse to send anything unless every BLOCKING safety check above passed. Non-blocking
    // (known, secondary) issues are still recorded in `steps` for visibility but don't gate.
    const blockingFailures = steps.filter((s) => !s.pass && !nonBlocking.has(s.name));
    if (blockingFailures.length > 0) {
      record('aborted-before-send', false, 'pre-send verification failed, refusing to send');
      return { pass: false, failures: blockingFailures.map((s) => s.name), steps };
    }

    // Send the instruction-chain prompt driving a real Bash tool call.
    await tid('chat-input-textarea').fill(PROMPT);
    await tid('send-button').click();

    // Wait for a terminal state: either an error banner or an assistant answer bubble.
    const result = await Promise.race([
      tid('error-banner')
        .waitFor({ state: 'visible', timeout: 60000 })
        .then(() => 'error'),
      tid('assistant-text')
        .first()
        .waitFor({ state: 'visible', timeout: 60000 })
        .then(() => 'assistant-text'),
    ]).catch(() => 'timeout');

    const errorBannerText = (await tid('error-banner').count()) > 0 ? await tid('error-banner').textContent() : null;

    // Expand the tool-call pill (if present) to reveal its Arguments/Result content.
    let toolPillText = null;
    const pillCount = await tid('tool-call-pill').count();
    if (pillCount > 0) {
      const pill = tid('tool-call-pill').first();
      await pill.locator('.item-header').first().click().catch(() => {});
      toolPillText = await pill.textContent();
    }

    const assistantText =
      (await tid('assistant-text').count()) > 0 ? await tid('assistant-text').first().textContent() : null;

    record('reached-terminal-state', result !== 'timeout', { result, errorBannerText, toolPillText, assistantText });

    // Final post-send verification: confirm the message really landed on OUR provisioned thread under
    // the mock provider — belt-and-suspenders after the bug where Send silently bound to a stale, real
    // conversation instead.
    const allConvos = await page.evaluate(async () => {
      const r = await fetch(`${location.origin}/api/conversations`);
      return r.ok ? r.json() : null;
    });
    const ours = Array.isArray(allConvos) ? allConvos.find((c) => c.threadId === threadId) : null;
    record(
      'post-send-provider-confirmed-mock',
      !!ours && (ours.provider === 'test' || ours.provider === 'test-anthropic'),
      { threadId, provider: ours && ours.provider }
    );
  } catch (e) {
    record('exception', false, String((e && e.stack) || e));
  }

  const failures = steps.filter((s) => !s.pass && !nonBlocking.has(s.name)).map((s) => s.name);
  return { pass: failures.length === 0, failures, steps };
}
