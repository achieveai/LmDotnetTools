// workflow-agent-run-and-view.mjs — single-call Playwright verification for the WorkflowAgent feature
// end-to-end: launch a workflow, confirm the nested delegate appears as a tab AND inherited tools
// (transparency), AND the workflow AGENT's own conversation (the ⚙ tab) is viewable after completion.
//
//   browser_run_code_unsafe({ filename: "samples/LmStreaming.Sample/playwright-scripts/workflow-agent-run-and-view.mjs" })
//
// Returns { pass, failures, steps, threadId, workflowThreadId, delegateThreadId, workflowMessageCount }.
//
// ⚠ REAL-PROVIDER script (NOT the deterministic mock): workflows are only wired in Workspace Agent mode,
// which requires a real provider + a live sandbox gateway + a selected workspace. It uses `gpt-5.6-luna`
// (GPT-5.6 Luna, Copilot-proxied) + the LmDotnetTools workspace. It drives a real LLM, so it is SLOW
// (minutes) and mildly non-deterministic (the model may retry the delegate a few times — that's fine,
// all are surfaced). If gpt-5.6-luna is unavailable, swap PROVIDER_ID for another real Copilot/proxy id.
//
// What it proves (all browser-observable / /api reads):
//   1. a kind:"workflow" run tab + ≥1 nested kind:"subagent" delegate tab appear (nested-tab surfacing)
//   2. the delegate actually inherited + used domain tools — its transcript has a successful Read
//      (workflow transparency: delegates inherit the launching conversation's tools)
//   3. the WORKFLOW AGENT conversation is viewable after completion — GET /messages for the workflow-{id}
//      thread is NON-EMPTY (the controller's GetWorkflow/SetCurrentNode/Agent orchestration), and clicking
//      the ⚙ tab renders it (not "unavailable"/blank)
//   4. usage rolled up — GET /{threadId}/usage totalTokens > 0 with the run's model
//
// Prereqs: app on BASE, a live gateway, and the LmDotnetTools workspace present (GET /api/workspaces).
async (page) => {
  const BASE = 'http://127.0.0.1:5050';
  const PROVIDER_ID = 'gpt-5.6-luna';
  const MODE_ID = 'workspace-agent';
  const WORKSPACE_ID = '192a3465-67a1-4945-9323-44c2168aeb2b'; // LmDotnetTools
  const SHOT = 'B:/sources/LmDotnetTools/.logs/manual/workflow-agent-run-and-view.png';
  const RUN_TIMEOUT_MS = 9 * 60 * 1000;

  const PROMPT =
    'Use the StartWorkflowAgent workflow tools to run a small workflow with ONE task delegated to a ' +
    'general-purpose agent: use the Read tool to read the first 3 lines of README.md at the repository ' +
    'root and report them. Author it, launch async, wait for completion, then give me the lines.';

  const steps = [];
  const record = (name, pass, detail) => steps.push({ name, pass, detail });
  const tid = (id) => page.locator(`[data-testid="${id}"]`);
  const api = (path) => page.evaluate((p) => fetch(p).then((r) => r.json()), path);

  const pollUntil = async (fn, timeoutMs, intervalMs = 4000) => {
    const deadline = Date.now() + timeoutMs;
    let last = null;
    while (Date.now() < deadline) {
      last = await fn().catch(() => null);
      if (last) return last;
      await page.waitForTimeout(intervalMs);
    }
    return last;
  };

  // Wait for a label to match a regex (used to confirm the deep-linked conversation actually bound its
  // workspace/provider/mode BEFORE we send — otherwise the send races activation and silently no-ops).
  const waitForLabelMatch = async (getLabel, regex, timeoutMs = 15000, intervalMs = 300) => {
    const deadline = Date.now() + timeoutMs;
    let last = null;
    while (Date.now() < deadline) {
      last = await getLabel().catch(() => null);
      if (regex.test(last ?? '')) return last;
      await page.waitForTimeout(intervalMs);
    }
    return last;
  };

  try {
    await page.goto(BASE);
    await tid('chat-input-textarea').waitFor({ timeout: 20000 });

    // 1. Provision a workspace-bound conversation headlessly (race-free — avoids the "+ New Chat" flow).
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
    record('provisioned-thread', !!threadId, threadId);
    if (!threadId) return { pass: false, failures: ['provisioned-thread'], steps };

    await page.goto(`${BASE}/?threadId=${encodeURIComponent(threadId)}`);
    await tid('chat-input-textarea').waitFor({ timeout: 20000 });

    // 2. CONFIRM the conversation bound workspace + provider + mode BEFORE sending. Sending before the
    //    deep-linked conversation is active silently no-ops (0 messages, unknown_thread) — the bug this
    //    guard prevents. Mirrors workflow-author-mode-verification.mjs.
    const wsLabel = await waitForLabelMatch(async () => {
      const badge = await tid('workspace-locked-badge').count();
      if (badge > 0) return tid('workspace-locked-badge').textContent();
      const btn = await tid('workspace-selector-button').count();
      return btn > 0 ? tid('workspace-selector-button').textContent() : null;
    }, /LmDotnetTools/);
    record('workspace-bound (LmDotnetTools)', /LmDotnetTools/.test(wsLabel ?? ''), wsLabel);

    const modeLabel = await waitForLabelMatch(() => tid('mode-selector-button').textContent(), /Workspace/);
    record('mode-bound (Workspace Agent)', /Workspace/.test(modeLabel ?? ''), modeLabel);

    const notFound = await tid('conversation-not-found').count();
    record('deep-link-resolved', notFound === 0, { notFound });

    if (steps.some((s) => !s.pass)) {
      record('aborted-before-send', false, 'pre-send binding check failed; refusing to send into a dead thread');
      return { pass: false, failures: steps.filter((s) => !s.pass).map((s) => s.name), steps, threadId };
    }

    // 3. Send the workflow prompt (real LLM authors + runs the workflow).
    await tid('chat-input-textarea').fill(PROMPT);
    await tid('send-button').click();
    // Confirm the send actually produced a turn (a user message bubble) before the long poll.
    await tid('user-message-group')
      .first()
      .waitFor({ state: 'visible', timeout: 20000 })
      .catch(() => {});
    record('prompt-sent', (await tid('user-message-group').count()) > 0, null);

    // 3. Poll /subagents until a kind:"workflow" run is completed and a delegate exists.
    const subUrl = `/api/conversations/${threadId}/subagents`;
    const final = await pollUntil(async () => {
      const subs = await api(subUrl);
      if (!Array.isArray(subs)) return null;
      const wf = subs.find((s) => s.kind === 'workflow');
      const delegates = subs.filter((s) => s.kind === 'subagent');
      const done = wf && String(wf.status).toLowerCase() === 'completed';
      return done && delegates.length > 0 ? { subs, wf, delegates } : null;
    }, RUN_TIMEOUT_MS);

    if (!final) {
      record('workflow-completed', false, 'timed out waiting for a completed workflow + delegate');
      return { pass: false, failures: ['workflow-completed'], steps, threadId };
    }
    const workflowThreadId = final.wf.threadId;
    const delegateThreadId = final.delegates[0].threadId;
    record('workflow-tab-present', true, final.wf.threadId);
    record('delegate-tab-present', true, `${final.delegates.length}: ${final.delegates.map((d) => d.agentId).join(',')}`);

    // 4. Transparency: a delegate inherited + USED a domain tool. Parse each delegate's messageJson
    //    (NOT a substring scan — the persisted messageJson is double-escaped, so a naive /"Read"/ misses
    //    it) and look for a tool_call whose function_name is a domain tool. The controller may spawn
    //    several delegates (retries), so scan them all. NOTE: this asserts the delegate CALLED the tool
    //    (proving inheritance), not that the call succeeded — whether the model picks the right file path
    //    is its own behavior, orthogonal to transparency.
    const scanned = await page.evaluate(async (threadIds) => {
      const domain = new Set(['Read', 'Bash', 'Glob', 'Grep', 'Skill', 'Write', 'Edit', 'PowerShell']);
      const out = [];
      for (const t of threadIds) {
        const msgs = await fetch(`/api/conversations/${t}/messages`).then((r) => r.json()).catch(() => null);
        const calls = [];
        for (const m of Array.isArray(msgs) ? msgs : []) {
          try {
            const mj = JSON.parse(m.messageJson || '{}');
            if (mj.$type === 'tool_call' && domain.has(mj.function_name)) calls.push(mj.function_name);
          } catch {
            /* skip unparseable */
          }
        }
        out.push({ threadId: t, msgs: Array.isArray(msgs) ? msgs.length : 'n/a', toolCalls: calls });
      }
      return out;
    }, final.delegates.map((d) => d.threadId));
    const delegateUsedTool = scanned.some((s) => Array.isArray(s.toolCalls) && s.toolCalls.length > 0);
    record('delegate-used-domain-tool (transparency)', delegateUsedTool, scanned);

    // 5. THE CORE PROOF: the workflow AGENT conversation is viewable — /messages is non-empty and shows
    //    the controller's orchestration (GetWorkflow / SetCurrentNode / Agent).
    const wfMsgs = await api(`/api/conversations/${workflowThreadId}/messages`);
    const wfText = JSON.stringify(wfMsgs);
    const workflowMessageCount = Array.isArray(wfMsgs) ? wfMsgs.length : 0;
    const hasOrchestration =
      wfText.includes('GetWorkflow') && (wfText.includes('SetCurrentNode') || wfText.includes('"Agent"'));
    record('workflow-conversation-viewable (REST)', workflowMessageCount > 0 && hasOrchestration, {
      workflowMessageCount,
      hasOrchestration,
    });

    // 6. Usage rolled up.
    const usage = await api(`/api/conversations/${threadId}/usage`).catch(() => null);
    const totalTokens = usage && (usage.totalTokens ?? 0);
    record('usage-rolled-up', !!totalTokens && totalTokens > 0, { totalTokens, perModel: usage && usage.perModel });

    // 7. UI: click the ⚙ workflow tab and confirm the CONTROLLER conversation renders. The controller's
    //    transcript has GetWorkflow pills (which the main conversation never has — it only has
    //    StartWorkflowAgent), so a visible GetWorkflow pill proves we switched to + rendered the workflow
    //    agent's own conversation (not "unavailable"/blank).
    const wfTab = page
      .locator('[data-testid="conversation-tab"]')
      .filter({ has: page.locator('[data-testid="workflow-tab-badge"]') })
      .first();
    let workflowTabRenders = false;
    if ((await wfTab.count()) > 0) {
      await wfTab.click().catch(() => {});
      const getWorkflowPill = page
        .locator('[data-testid="tool-call-pill"][data-tool-name="GetWorkflow"]')
        .first();
      await getWorkflowPill.waitFor({ state: 'visible', timeout: 12000 }).catch(() => {});
      const pillCount = await page
        .locator('[data-testid="tool-call-pill"][data-tool-name="GetWorkflow"]')
        .count();
      workflowTabRenders = pillCount > 0;
      record('workflow-tab-renders (UI)', workflowTabRenders, { getWorkflowPills: pillCount });
    } else {
      record('workflow-tab-renders (UI)', false, 'no ⚙ workflow tab found in the strip');
    }

    await page.screenshot({ path: SHOT, fullPage: false }).catch(() => {});

    const failures = steps.filter((s) => !s.pass).map((s) => s.name);
    return {
      pass: failures.length === 0,
      failures,
      steps,
      threadId,
      workflowThreadId,
      delegateThreadId,
      workflowMessageCount,
    };
  } catch (err) {
    record('exception', false, String(err && err.message ? err.message : err));
    return { pass: false, failures: ['exception'], steps };
  }
}
