// subagent-ordinal-ids.mjs — single-call Playwright verification for #705: sub-agent ids are ORDINAL
// ("agent-1", "agent-2", ...) in strict spawn order under the ROOT conversation, the child transcript
// thread id is `subagent-{12 lowercase hex scope}-agent-N` (scope = digest of the root thread id, so
// all children of one root share it), and the counter PERSISTS across a backend restart (a restarted
// server continues at agent-N+1 instead of re-minting agent-1).
//
// Two phases, gated by EXISTING_THREAD (a script cannot restart the server — see README "Durability
// across restart is NOT scriptable"):
//
//   Phase 1 (EXISTING_THREAD === ''):  fresh chat on the mock provider, background-spawn alpha + beta
//     (PromptExamples.md → "Sub-Agent Tabs" → "Two sub-agents → two distinct colored tabs") and assert
//     alpha=agent-1 / beta=agent-2, thread-id shape, shared scope, and that the alpha tab renders alpha's
//     transcript over /ws/subagent with the scoped thread id. Returns the root threadId in `steps`.
//   >>> operator restarts the BACKEND (keep Vite) <<<
//   Phase 2 (EXISTING_THREAD = '<threadId from phase 1>', PRIOR_ROWS filled from phase 1): deep-link
//     `?threadId=`, assert the persisted roster still lists agent-1/agent-2 with the SAME thread ids,
//     then background-spawn ONE more worker (gamma — same shape as the alpha spawn, one Agent call) and
//     assert the new row is agent-3 (NOT agent-1) under the SAME scope, and its tab renders its text.
//
//   browser_run_code_unsafe({ filename: "samples/LmStreaming.Sample/playwright-scripts/subagent-ordinal-ids.mjs" })
//
// Returns { pass, failures, steps }. Uses the MOCK provider `test-anthropic` only (no real LLM). Assert
// only DETERMINISTIC, browser-observable state (DOM testids + /api/* reads via the Vite proxy).
async (page) => {
  const BASE = 'http://localhost:5173/dist/';
  const PROVIDER = 'test-anthropic';
  const SHOT_DIR = 'B:/sources/LmDotnetTools/.claude/worktrees/lmstreaming-dropped-conversations-f9dc50/.logs/manual';

  // Phase gate. '' => phase 1. Otherwise the root threadId returned by phase 1 (after a backend restart).
  // e.g. 'thread-1788340192311-07bbb1e101c14b4c8b31c1ef57eadf97'
  const EXISTING_THREAD = '';
  // Phase 2 only: the { agentId: threadId } rows phase 1 observed, so "same threadIds as before" is an
  // assertion rather than a by-eye comparison.
  // e.g. { 'agent-1': 'subagent-6b7dd702755e-agent-1', 'agent-2': 'subagent-6b7dd702755e-agent-2' }
  const PRIOR_ROWS = {};

  const THREAD_RE = /^subagent-([0-9a-f]{12})-(agent-\d+)$/;
  const scopeOf = (threadId) => (THREAD_RE.exec(threadId ?? '') ?? [])[1] ?? null;

  // Prompts: PromptExamples.md → "Sub-Agent Tabs" → "Two sub-agents → two distinct colored tabs"
  // (identical to subagent-tabs.mjs), built with JSON.stringify so nested-chain escaping is right by
  // construction.
  const innerAlpha = JSON.stringify({
    instruction_chain: [
      { id: 'a1', messages: [{ text: 'Alpha reporting: I found three fresh AI papers today.' }] },
    ],
  });
  const innerBeta = JSON.stringify({
    instruction_chain: [
      { id: 'b1', messages: [{ tool_call: [{ name: 'calculate', args: { a: 40, operation: 'add', b: 2 } }] }] },
      { id: 'b2', messages: [{ text: 'Beta reporting: 40 + 2 = 42.' }] },
    ],
  });
  const spawnTwo = {
    instruction_chain: [
      {
        id: 'spawn-two',
        id_message: 'Spawn two background workers',
        messages: [
          {
            tool_call: [
              { name: 'Agent', args: { subagent_type: 'researcher', name: 'alpha', run_in_background: true, prompt: `<|instruction_start|>${innerAlpha}<|instruction_end|>` } },
              { name: 'Agent', args: { subagent_type: 'general-purpose', name: 'beta', run_in_background: true, prompt: `<|instruction_start|>${innerBeta}<|instruction_end|>` } },
            ],
          },
        ],
      },
      { id: 'parent-done', id_message: 'Wrap up', messages: [{ text: 'Spawned alpha and beta in the background.' }] },
    ],
  };
  // Phase 2 spawn: the alpha shape (one text-only background worker), renamed gamma so the third
  // ordinal is attributable by name. Same "Sub-Agent Tabs" prompt family.
  const GAMMA_TEXT = 'Gamma reporting: third worker online after restart.';
  const innerGamma = JSON.stringify({
    instruction_chain: [{ id: 'g1', messages: [{ text: GAMMA_TEXT }] }],
  });
  const spawnGamma = {
    instruction_chain: [
      {
        id: 'spawn-gamma',
        id_message: 'Spawn one more background worker',
        messages: [
          {
            tool_call: [
              { name: 'Agent', args: { subagent_type: 'researcher', name: 'gamma', run_in_background: true, prompt: `<|instruction_start|>${innerGamma}<|instruction_end|>` } },
            ],
          },
        ],
      },
      { id: 'gamma-done', id_message: 'Wrap up', messages: [{ text: 'Spawned gamma in the background.' }] },
    ],
  };
  const wrap = (chain) => `<|instruction_start|>${JSON.stringify(chain)}<|instruction_end|>`;

  const steps = [];
  const record = (name, pass, detail) => steps.push({ name, pass, detail });
  const tid = (id) => page.locator(`[data-testid="${id}"]`);
  const shot = (name) => page.screenshot({ path: `${SHOT_DIR}/pw705-${name}.png` }).catch(() => {});
  const waitIdle = async () => {
    await tid('stop-button').waitFor({ state: 'hidden', timeout: 90000 });
    await tid('send-button').waitFor({ state: 'visible', timeout: 90000 });
  };
  const send = async (text) => {
    await tid('chat-input-textarea').fill(text);
    await tid('send-button').click();
  };
  const subTabs = () => page.locator('[data-testid="conversation-tab"]:not([data-tab-id="main"])');
  const tabSummary = () =>
    page.$$eval('[data-testid="conversation-tab"]', (els) =>
      els.map((e) => ({ id: e.getAttribute('data-tab-id'), kind: e.getAttribute('data-tab-kind'), label: e.textContent.trim() }))
    );
  // The ACTIVE sidebar row is the conversation we just drove (the sidebar is sorted newest-first but
  // the active class is the unambiguous signal).
  const activeThreadId = () =>
    page.evaluate(
      () =>
        document.querySelector('[data-testid=conversation-item].active')?.getAttribute('data-thread-id') ??
        document.querySelector('[data-testid=conversation-item]')?.getAttribute('data-thread-id') ??
        null
    );
  // Poll /subagents IN-PAGE until at least `min` rows are present — state-based (waitForFunction), no timers.
  const waitForRows = async (threadId, min, timeoutMs = 30000) => {
    const handle = await page.waitForFunction(
      async ({ t, n }) => {
        const res = await fetch(`/api/conversations/${t}/subagents`);
        if (!res.ok) return null;
        const body = await res.json();
        const list = Array.isArray(body) ? body : body.subAgents || body.subagents || [];
        return list.length >= n ? list : null;
      },
      { t: threadId, n: min },
      { polling: 500, timeout: timeoutMs }
    );
    const rows = await handle.jsonValue();
    return rows.map((r) => ({
      agentId: r.agentId ?? r.AgentId,
      name: r.name ?? r.Name,
      threadId: r.threadId ?? r.ThreadId,
      status: r.status ?? r.Status,
      kind: r.kind ?? r.Kind,
    }));
  };
  const clickTabAndAssertText = async (label, text, stepName, shotName) => {
    await subTabs().filter({ hasText: label }).first().click();
    await tid('subagent-view').waitFor({ state: 'visible', timeout: 10000 });
    await tid('subagent-transcript').getByText(text, { exact: true }).first().waitFor({ timeout: 20000 });
    record(stepName, true, { label, text });
    await shot(shotName);
  };

  try {
    if (!EXISTING_THREAD) {
      // ---------------- Phase 1: fresh conversation, alpha + beta => agent-1 / agent-2 ----------------
      await page.goto(BASE);
      await tid('chat-input-textarea').waitFor({ timeout: 20000 });
      await page.getByRole('button', { name: '+ New Chat' }).click();
      await tid('clear-button').click().catch(() => {});
      await tid('provider-selector-button').click();
      await tid(`provider-option-${PROVIDER}`).click();

      await send(wrap(spawnTwo));
      await waitIdle();
      record('phase1: spawn prompt sent and run idle', true);

      const threadId = await activeThreadId();
      record('phase1: root threadId read from sidebar', !!threadId, { threadId });

      const rows = await waitForRows(threadId, 2);
      const byName = Object.fromEntries(rows.map((r) => [r.name, r]));
      const ids = rows.map((r) => r.agentId).sort();
      record('phase1: exactly two rows with agentIds agent-1, agent-2',
        rows.length === 2 && ids.join(',') === 'agent-1,agent-2', { rows });
      record('phase1: name<->id pairing alpha=agent-1, beta=agent-2',
        byName.alpha?.agentId === 'agent-1' && byName.beta?.agentId === 'agent-2',
        { alpha: byName.alpha?.agentId, beta: byName.beta?.agentId });
      const scopes = rows.map((r) => scopeOf(r.threadId));
      record('phase1: threadIds match /^subagent-[0-9a-f]{12}-agent-N$/ and share one scope',
        rows.every((r) => THREAD_RE.test(r.threadId))
          && rows.every((r) => r.threadId === `subagent-${scopes[0]}-${r.agentId}`)
          && scopes[0] !== null && scopes.every((s) => s === scopes[0]),
        { threadIds: rows.map((r) => r.threadId), scope: scopes[0] });

      // Tabs: main + alpha + beta; alpha's tab renders alpha's transcript over /ws/subagent.
      await subTabs().nth(1).waitFor({ state: 'attached', timeout: 20000 });
      const tabs = await tabSummary();
      const subTabIds = tabs.filter((t) => t.id !== 'main').map((t) => t.id).sort();
      record('phase1: two sub-agent tabs in the strip (alpha, beta), tab ids = agentIds',
        subTabIds.length === 2 && subTabIds.join(',') === 'agent-1,agent-2'
          && tabs.some((t) => t.label.includes('alpha')) && tabs.some((t) => t.label.includes('beta')),
        { tabs });
      await shot('p1-01-tabs-on-main');
      await clickTabAndAssertText('alpha', 'Alpha reporting: I found three fresh AI papers today.',
        'phase1: alpha tab renders alpha transcript (scoped thread id over /ws/subagent)', 'p1-02-alpha-tab');
      await clickTabAndAssertText('beta', 'Beta reporting: 40 + 2 = 42.',
        'phase1: beta tab renders beta transcript', 'p1-03-beta-tab');

      record('phase1: handoff for phase 2 (set EXISTING_THREAD + PRIOR_ROWS)', true, {
        EXISTING_THREAD: threadId,
        PRIOR_ROWS: Object.fromEntries(rows.map((r) => [r.agentId, r.threadId])),
      });
    } else {
      // ---------------- Phase 2: after backend restart, reopen + spawn gamma => agent-3 ----------------
      await page.goto(`${BASE}?threadId=${encodeURIComponent(EXISTING_THREAD)}`);
      await tid('chat-input-textarea').waitFor({ timeout: 20000 });
      // History loaded: both of phase 1's Agent-call pills are rehydrated in the main view.
      const agentPills = () => page.locator('[data-testid="main-view"] [data-testid="tool-call-pill"][data-tool-name="Agent"]');
      await agentPills().nth(1).waitFor({ state: 'attached', timeout: 30000 });
      // Observation only (not a #705 assertion): which text bubbles the main pane rehydrates. The
      // parent's own wrap-up text is persisted in messages.json but was NOT rendered in phase 1 either.
      const mainTexts = await page.$$eval('[data-testid="main-view"] [data-testid="assistant-text"]', (els) => els.map((e) => e.textContent.trim()));
      record('phase2: deep-linked conversation history loaded (2 Agent pills)', true, { EXISTING_THREAD, agentPills: await agentPills().count(), mainTexts });
      const activeId = await activeThreadId();
      record('phase2: sidebar active row is the deep-linked thread', activeId === EXISTING_THREAD, { activeId });

      const before = await waitForRows(EXISTING_THREAD, 2);
      const beforeIds = before.map((r) => r.agentId).sort();
      record('phase2: persisted roster survives restart (agent-1, agent-2, same threadIds)',
        before.length === 2 && beforeIds.join(',') === 'agent-1,agent-2'
          && before.every((r) => PRIOR_ROWS[r.agentId] === r.threadId),
        { before, PRIOR_ROWS });
      await subTabs().nth(1).waitFor({ state: 'attached', timeout: 20000 });
      record('phase2: alpha/beta tabs present after restart', true, { tabs: await tabSummary() });
      await shot('p2-01-after-restart');

      await send(wrap(spawnGamma));
      await waitIdle();
      record('phase2: gamma spawn prompt sent and run idle', true);

      const after = await waitForRows(EXISTING_THREAD, 3);
      const gamma = after.find((r) => r.name === 'gamma');
      const priorScope = scopeOf(Object.values(PRIOR_ROWS)[0]);
      const afterIds = after.map((r) => r.agentId).sort();
      record('phase2: three rows, ids exactly agent-1, agent-2, agent-3 (no duplicate agent-1)',
        after.length === 3 && afterIds.join(',') === 'agent-1,agent-2,agent-3', { after });
      record('phase2: gamma is agent-3 with threadId subagent-{same scope}-agent-3',
        !!gamma && gamma.agentId === 'agent-3' && gamma.threadId === `subagent-${priorScope}-agent-3`,
        { gamma, priorScope, expected: `subagent-${priorScope}-agent-3` });
      record('phase2: agent-1/agent-2 rows unchanged after the new spawn',
        after.filter((r) => r.agentId !== 'agent-3').every((r) => PRIOR_ROWS[r.agentId] === r.threadId),
        { unchanged: after.filter((r) => r.agentId !== 'agent-3') });

      await subTabs().nth(2).waitFor({ state: 'attached', timeout: 20000 });
      const tabs = await tabSummary();
      record('phase2: gamma tab appears in the strip with tab id agent-3',
        tabs.some((t) => t.id === 'agent-3' && t.label.includes('gamma')), { tabs });
      await clickTabAndAssertText('gamma', GAMMA_TEXT,
        'phase2: gamma tab renders gamma transcript (scoped thread id over /ws/subagent)', 'p2-02-gamma-tab');
    }
  } catch (e) {
    record('exception', false, String((e && e.stack) || e));
    await shot(EXISTING_THREAD ? 'p2-99-error' : 'p1-99-error');
  }

  const failures = steps.filter((s) => !s.pass).map((s) => s.name);
  return { pass: failures.length === 0, failures, steps };
}
