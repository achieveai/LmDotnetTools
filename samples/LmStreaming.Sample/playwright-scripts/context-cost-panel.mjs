// context-cost-panel.mjs — single-call Playwright check of the context / cost / compaction panel
// (#685, spec 679 §7): one row per framework-owned agent + one conversation total; zero is never
// rendered as unknown; live frames and a reload converge on the endpoint's values; the panel works
// at desktop AND phone widths. Run in ONE call:
//
//   browser_run_code_unsafe({ filename: "samples/LmStreaming.Sample/playwright-scripts/context-cost-panel.mjs" })
//
// Returns { pass, failures, steps, shots }. Assert only DETERMINISTIC, browser-observable state
// (data-testid + /api reads). Prompts: PromptExamples.md "Context / cost panel (#685) UI tests".
//
// Mock providers only:
//   * `test-anthropic` runs as `claude-sonnet-4-5-20250929`, which appsettings.json prices with a
//     200,000-token window => the loop publishes `context_pressure` frames and the row shows "N% of
//     200,000 tokens". Every generation is 100 in / 50 out.
//   * `test` runs as `test-model`, which has NO configured window => the row must read "Unknown
//     window" — NOT 0%. That is the zero-vs-unknown assertion. It also persists no usage row, so the
//     same row must read "No usage recorded" rather than "$0.0000".
//
// Scenarios (each screenshot goes to .logs/manual/pw685-*.png under the worktree):
//   A. single agent with pressure (test-anthropic)            -> pw685-single.png
//   B. parent + 2 background sub-agents: 3 rows + total       -> pw685-subagents.png
//   C. unknown window (test provider)                         -> pw685-unknown.png
//   D. reload parity: rows after ?threadId= reload == endpoint -> pw685-reload.png
//   E. phone width (390): cells stack, nothing overflows       -> pw685-mobile.png
async (page) => {
  const BASE = 'http://localhost:5685';
  const SHOT_DIR = 'B:/sources/LmDotnetTools/.claude/worktrees/wi-685/.logs/manual';
  const shots = {};

  const steps = [];
  const record = (name, pass, detail) => steps.push({ name, pass, detail });
  const tid = (id) => page.locator(`[data-testid="${id}"]`);
  const chain = (obj) => `<|instruction_start|>${JSON.stringify(obj)}<|instruction_end|>`;

  const SIMPLE = chain({
    instruction_chain: [{ id: 't1', id_message: 'Say hello', messages: [{ text: 'Hello from the context panel check.' }] }],
  });
  const child = (name) =>
    chain({ instruction_chain: [{ id: `${name}-text`, messages: [{ text: `${name} done.` }] }] });
  const TWO_SUBAGENTS = chain({
    instruction_chain: [
      {
        id: 'spawn',
        id_message: 'Spawn two background sub-agents',
        messages: [
          {
            tool_call: [
              { name: 'Agent', args: { subagent_type: 'general-purpose', name: 'alpha', run_in_background: true, prompt: child('alpha') } },
              { name: 'Agent', args: { subagent_type: 'researcher', name: 'beta', run_in_background: true, prompt: child('beta') } },
            ],
          },
        ],
      },
      { id: 'done', id_message: 'Wrap up', messages: [{ text: 'Both sub-agents spawned.' }] },
    ],
  });

  const send = async (text) => {
    await tid('chat-input-textarea').fill(text);
    await tid('send-button').click();
  };
  const providerLabelOk = async (re) =>
    re.test((await tid('provider-selector-button').textContent().catch(() => '')) ?? '');
  const selectProvider = async (providerId, re) => {
    for (let attempt = 0; attempt < 5; attempt++) {
      try {
        await tid('provider-selector-button').click();
        const opt = tid(`provider-option-${providerId}`);
        await opt.waitFor({ state: 'visible', timeout: 8000 });
        await page.waitForTimeout(350);
        await opt.click({ timeout: 5000 });
        if (await providerLabelOk(re)) return true;
      } catch {
        await page.keyboard.press('Escape').catch(() => {});
        await page.waitForTimeout(300);
      }
    }
    return providerLabelOk(re);
  };
  const threadIdsFromApi = async () =>
    page.evaluate(async () => {
      const r = await fetch('/api/conversations');
      const body = await r.json();
      const list = body.conversations ?? body ?? [];
      return list.map((c) => c.threadId);
    });
  const contextFor = async (id) =>
    page.evaluate(async (threadId) => {
      const r = await fetch(`/api/conversations/${threadId}/context`);
      return r.ok ? await r.json() : { status: r.status };
    }, id);
  const subAgentsFor = async (id) =>
    page.evaluate(async (threadId) => {
      const r = await fetch(`/api/conversations/${threadId}/subagents`);
      return r.ok ? await r.json() : { status: r.status };
    }, id);
  const waitRunStartThenIdle = async (timeout = 45000) => {
    const deadline = Date.now() + timeout;
    let started = false;
    while (Date.now() < deadline) {
      const stopVisible = await tid('stop-button').isVisible().catch(() => false);
      const sendVisible = await tid('send-button').isVisible().catch(() => false);
      if (stopVisible) started = true;
      if (started && !stopVisible && sendVisible) return true;
      await page.waitForTimeout(150);
    }
    return started;
  };
  const isEmptyChat = async () =>
    (await page.locator('[data-testid="user-message-group"]').count()) === 0 &&
    (await tid('usage-banner').count()) === 0;
  const freshChat = async () => {
    for (let i = 0; i < 5; i++) {
      await page.getByRole('button', { name: '+ New Chat' }).click().catch(() => {});
      await page.waitForTimeout(500);
      if (await isEmptyChat()) return true;
    }
    return false;
  };
  const newThreadAfter = async (beforeIds) => {
    for (let i = 0; i < 25; i++) {
      const after = await threadIdsFromApi();
      const id = after.find((x) => !beforeIds.has(x));
      if (id) return id;
      await page.waitForTimeout(200);
    }
    return null;
  };
  const expandPanel = async () => {
    const toggle = tid('context-panel-toggle');
    await toggle.waitFor({ timeout: 10000 });
    if ((await toggle.getAttribute('aria-expanded')) !== 'true') await toggle.click();
    await tid('context-table').waitFor({ timeout: 10000 }).catch(() => {});
  };
  /** Reads the rendered rows into plain data so live and reload views can be compared. */
  const readRows = async () =>
    page.evaluate(() => {
      const rows = [...document.querySelectorAll('[data-testid="context-row"]')].map((r) => ({
        agentId: r.getAttribute('data-agent-id'),
        provisional: r.getAttribute('data-provisional'),
        capacity: r.querySelector('[data-testid="context-capacity"]')?.textContent?.trim(),
        capacityKind: r.querySelector('[data-testid="context-capacity"]')?.getAttribute('data-kind'),
        meterNow: r.querySelector('[role="meter"]')?.getAttribute('aria-valuenow') ?? null,
        meterText: r.querySelector('[role="meter"]')?.getAttribute('aria-valuetext') ?? null,
        tokens: r.querySelector('[data-testid="context-tokens"]')?.textContent?.trim(),
        cost: r.querySelector('[data-testid="context-cost"]')?.textContent?.trim(),
        freshness: r.querySelector('[data-testid="context-freshness"]')?.textContent?.trim(),
        temperature: r.querySelector('[data-testid="context-temperature"]')?.textContent?.trim(),
      }));
      const total = {
        tokens: document.querySelector('[data-testid="context-total-tokens"]')?.textContent?.trim(),
        cost: document.querySelector('[data-testid="context-total-cost"]')?.textContent?.trim(),
        completeness: document.querySelector('[data-testid="context-total-completeness"]')?.textContent?.trim(),
      };
      const summary = document.querySelector('[data-testid="context-panel-summary"]')?.textContent?.trim();
      const status = document.querySelector('[data-testid="context-panel"]')?.getAttribute('data-status');
      return { rows, total, summary, status };
    });
  /** Polls until the rendered rows satisfy `predicate`, then returns them. */
  const waitRows = async (predicate, timeout = 30000) => {
    const deadline = Date.now() + timeout;
    let view = await readRows();
    while (Date.now() < deadline && !predicate(view)) {
      await page.waitForTimeout(250);
      view = await readRows();
    }
    return view;
  };
  const fmt = (n) => Number(n).toLocaleString('en-US');
  const usd = (micros) => `$${(micros / 1_000_000).toFixed(4)}`;
  /** What the panel must say for one endpoint row (mirrors src/utils/contextReport.ts). */
  const expectedCapacity = (agent) => {
    const o = agent.observation;
    if (!o) return agent.compaction?.state === 'Unsupported' ? 'Unsupported (provider-owned session)' : 'No observation';
    const w = o.window_tokens;
    const usable = (w ?? 0) - (o.reserve_tokens ?? 0);
    if (!w || w <= 0 || usable <= 0) return 'Unknown window';
    const used = o.measured_input_tokens ?? o.estimated_input_tokens;
    const pct = (used / usable) * 100;
    const pctText = pct > 0 && pct < 10 ? `${pct.toFixed(1)}%` : `${Math.round(pct)}%`;
    return `${pctText} of ${fmt(w)} tokens (${String(o.provenance).toLowerCase()})`;
  };
  const expectedCost = (usage) => {
    if (!usage) return 'No usage recorded';
    if (usage.preferredCostMicros == null || usage.costProvenance === 'Unavailable') return 'Unavailable';
    if (usage.costProvenance === 'ProviderReported') return `${usd(usage.preferredCostMicros)} (provider-reported)`;
    const completeness = usage.estimatedCostCompleteness === 'Partial' ? 'partial — lower bound' : 'complete';
    return `${usd(usage.preferredCostMicros)} (public estimate, ${completeness})`;
  };
  const shot = async (name) => {
    const path = `${SHOT_DIR}/pw685-${name}.png`;
    await page.screenshot({ path, fullPage: false }).catch(() => {});
    shots[name] = path;
  };

  let singleThread = null;
  let subThread = null;
  let unknownThread = null;

  try {
    await page.setViewportSize({ width: 1280, height: 800 });
    await page.goto(BASE);
    await tid('chat-input-textarea').waitFor({ timeout: 20000 });
    await page.locator('[data-testid="conversation-item"]').first().waitFor({ timeout: 20000 }).catch(() => {});
    await page.waitForTimeout(1200);

    // A fresh, unsent chat has not started: the panel must not be mounted yet (nothing to fetch).
    const fresh = await freshChat();
    record('fresh chat mounts no context panel', fresh && (await tid('context-panel').count()) === 0, { fresh });

    // ---- A. single agent with pressure ------------------------------------------------------
    const providerA = await selectProvider('test-anthropic', /Anthropic/i);
    let before = new Set(await threadIdsFromApi());
    await send(SIMPLE);
    const ranA = await waitRunStartThenIdle();
    singleThread = await newThreadAfter(before);
    record('A: run on test-anthropic reached idle in a new thread', providerA && ranA && !!singleThread, {
      providerA, ranA, singleThread,
    });

    await expandPanel();
    const viewA = await waitRows((v) => v.rows.length === 1 && v.rows[0].cost?.startsWith('$'));
    const rowA = viewA.rows[0] ?? {};
    record('A: exactly one row (the main agent) with a KNOWN window and a live meter', viewA.rows.length === 1 &&
      rowA.agentId === 'root' && rowA.capacityKind === 'known' && /% of 200,000 tokens \((estimated|measured)\)$/.test(rowA.capacity ?? '') &&
      rowA.meterNow !== null && rowA.meterText === rowA.capacity,
      rowA);
    record('A: usage and cost are values, not unknown', /^\d[\d,]* tokens$/.test(rowA.tokens ?? '') && /^\$\d/.test(rowA.cost ?? ''),
      { tokens: rowA.tokens, cost: rowA.cost });
    record('A: total row present with the same tokens and a completeness word', /tokens$/.test(viewA.total.tokens ?? '') &&
      /^\$\d/.test(viewA.total.cost ?? '') && /Usage (complete|in progress|not persisted)/.test(viewA.total.completeness ?? ''),
      viewA.total);
    const ctxA = await contextFor(singleThread);
    record('A: rendered capacity/cost equal the endpoint report (live == authoritative)',
      Array.isArray(ctxA.agents) && ctxA.agents.length === 1 &&
      expectedCapacity(ctxA.agents[0]) === rowA.capacity && expectedCost(ctxA.agents[0].usage) === rowA.cost &&
      expectedCost({ preferredCostMicros: ctxA.total?.preferredCostMicros, costProvenance: ctxA.total?.costProvenance,
        estimatedCostCompleteness: ctxA.total?.costCompleteness }) === viewA.total.cost,
      { expected: ctxA.agents?.map(expectedCapacity), rendered: rowA.capacity, expectedCost: ctxA.agents?.map((a) => expectedCost(a.usage)), renderedCost: rowA.cost, totalCost: viewA.total.cost, endpointTotal: ctxA.total });
    // Zero-vs-unknown, negative side: nothing on a priced, observed row may read as unknown.
    record('A: no "Unknown"/"No observation" wording on an observed row', !/Unknown|No observation|Unsupported/.test(`${rowA.capacity} ${rowA.cost} ${rowA.tokens}`), rowA);
    // Default view exposes no prompt/message content.
    const panelText = await tid('context-panel').textContent();
    record('A: panel renders no prompt content', !panelText.includes('Hello from the context panel check'), { length: panelText.length });
    await shot('single');

    // ---- B. parent + two background sub-agents ------------------------------------------------
    await freshChat();
    await selectProvider('test-anthropic', /Anthropic/i);
    before = new Set(await threadIdsFromApi());
    await send(TWO_SUBAGENTS);
    const ranB = await waitRunStartThenIdle();
    subThread = await newThreadAfter(before);
    record('B: delegation run reached idle in a new thread', ranB && !!subThread, { ranB, subThread });

    // Children run on their own loops; wait for both to reach a terminal status on the server.
    let children = [];
    for (let i = 0; i < 120; i++) {
      const list = await subAgentsFor(subThread);
      children = Array.isArray(list) ? list : list?.subAgents ?? list?.agents ?? [];
      if (children.length >= 2 && children.every((c) => /completed|failed|interrupted|cancel/i.test(c.status ?? ''))) break;
      await page.waitForTimeout(250);
    }
    record('B: two sub-agents exist and finished', children.length >= 2, children.map((c) => ({ agentId: c.agentId, status: c.status })));

    await expandPanel();
    const viewB = await waitRows((v) => v.rows.length >= 3);
    const ids = viewB.rows.map((r) => r.agentId);
    record('B: three rows — main agent first, then both sub-agents', viewB.rows.length === 3 && ids[0] === 'root' &&
      children.every((c) => ids.includes(c.agentId)), { ids, children: children.map((c) => c.agentId) });
    record('B: every row has a distinct-state capacity label', viewB.rows.every((r) => r.capacity && r.capacityKind), viewB.rows);
    await shot('subagents');

    // ---- D. reload parity (on the sub-agent conversation, the richer of the two) ---------------
    const ctxB = await contextFor(subThread);
    await page.goto(`${BASE}/?threadId=${subThread}`);
    await tid('chat-input-textarea').waitFor({ timeout: 20000 });
    await expandPanel();
    const viewD = await waitRows((v) => v.rows.length >= 3 && v.status === 'ready');
    const byAgent = Object.fromEntries(viewD.rows.map((r) => [r.agentId, r]));
    const parity = Array.isArray(ctxB.agents) && ctxB.agents.every((a) => {
      const r = byAgent[a.agentId];
      return r && r.capacity === expectedCapacity(a) && r.cost === expectedCost(a.usage) && r.provisional === null;
    });
    const expectedTotalCost = expectedCost({ preferredCostMicros: ctxB.total?.preferredCostMicros,
      costProvenance: ctxB.total?.costProvenance, estimatedCostCompleteness: ctxB.total?.costCompleteness });
    record('D: after reload every row equals the endpoint report, and no row is provisional', parity && viewD.total.cost === expectedTotalCost,
      { rendered: viewD.rows.map((r) => [r.agentId, r.capacity, r.cost]), expected: ctxB.agents?.map((a) => [a.agentId, expectedCapacity(a), expectedCost(a.usage)]), totalCost: viewD.total.cost, expectedTotalCost });
    record('D: total tokens == endpoint total', viewD.total.tokens === `${fmt(ctxB.total?.totalTokens ?? -1)} tokens`,
      { rendered: viewD.total.tokens, endpoint: ctxB.total?.totalTokens });
    record('D: sub-agent rows carry the sub-agent usage (no "No usage recorded")',
      viewD.rows.slice(1).every((r) => /^\d/.test(r.tokens ?? '')), viewD.rows.map((r) => [r.agentId, r.tokens]));
    await shot('reload');

    // Keyboard reachability: Tab from the panel toggle lands on a row's details button, ArrowDown moves it.
    await tid('context-panel-toggle').focus();
    await page.keyboard.press('Tab');
    const focused1 = await page.evaluate(() => document.activeElement?.getAttribute('data-testid') ?? null);
    await page.keyboard.press('ArrowDown');
    const focused2 = await page.evaluate(() => document.activeElement?.getAttribute('aria-label') ?? null);
    await page.keyboard.press('Enter');
    const detailsOpen = (await tid('context-row-details').count()) === 1;
    record('D: keyboard — Tab reaches a row, ArrowDown moves rows, Enter opens details', focused1 === 'context-row-details-toggle' && !!focused2 && detailsOpen,
      { focused1, focused2, detailsOpen });

    // ---- C. unknown window (test provider) ----------------------------------------------------
    await page.goto(BASE);
    await tid('chat-input-textarea').waitFor({ timeout: 20000 });
    await page.waitForTimeout(1200);
    await freshChat();
    const providerC = await selectProvider('test', /Test \(Mock\)/i);
    before = new Set(await threadIdsFromApi());
    await send(SIMPLE);
    const ranC = await waitRunStartThenIdle();
    unknownThread = await newThreadAfter(before);
    record('C: run on the test provider reached idle in a new thread', providerC && ranC && !!unknownThread, { providerC, ranC, unknownThread });
    await expandPanel();
    const viewC = await waitRows((v) => v.rows.length === 1 && v.rows[0].cost && !/loading/i.test(v.summary ?? ''));
    const rowC = viewC.rows[0] ?? {};
    record('C: unknown window renders as "Unknown window" with NO meter — never 0%', rowC.capacityKind === 'no-window' &&
      rowC.capacity === 'Unknown window' && rowC.meterNow === null && !/0%/.test(rowC.capacity ?? ''), rowC);
    // The `test` mock persists NO usage row (its /usage answers 404), so the row must say so in words
    // — "No usage recorded", never "$0.0000" — and must agree with what the endpoint reports.
    const ctxC = await contextFor(unknownThread);
    const usageC = ctxC.agents?.[0]?.usage ?? null;
    record('C: usage/cost wording equals the endpoint (no usage row => "No usage recorded", not $0)',
      rowC.cost === expectedCost(usageC) && (usageC ? /^\d[\d,]* tokens$/.test(rowC.tokens ?? '') : rowC.tokens === 'No usage recorded') &&
      rowC.cost !== '$0.0000 (public estimate, complete)',
      { tokens: rowC.tokens, cost: rowC.cost, endpointUsage: usageC, endpointTotal: ctxC.total });
    await shot('unknown');

    // ---- E. phone width ------------------------------------------------------------------------
    await page.setViewportSize({ width: 390, height: 844 });
    await page.goto(`${BASE}/?threadId=${subThread}`);
    await tid('chat-input-textarea').waitFor({ timeout: 20000 });
    await expandPanel();
    await waitRows((v) => v.rows.length >= 3);
    const mobile = await page.evaluate(() => {
      const td = document.querySelector('[data-testid="context-row"] td');
      const table = document.querySelector('[data-testid="context-table"]');
      const panel = document.querySelector('[data-testid="context-panel"]');
      return {
        cellDisplay: td ? getComputedStyle(td).display : null,
        tableWidth: table?.getBoundingClientRect().width ?? null,
        panelWidth: panel?.getBoundingClientRect().width ?? null,
        docOverflow: document.documentElement.scrollWidth > document.documentElement.clientWidth,
      };
    });
    record('E: at 390px the cells stack (display:block) and the page does not scroll sideways',
      mobile.cellDisplay === 'block' && mobile.docOverflow === false && mobile.tableWidth !== null && mobile.tableWidth <= 390, mobile);
    await shot('mobile');
    await page.setViewportSize({ width: 1280, height: 800 });
  } catch (e) {
    record('exception', false, String((e && e.stack) || e));
    await shot('exception');
  }

  const failures = steps.filter((s) => !s.pass).map((s) => s.name);
  return { pass: failures.length === 0, failures, steps, shots, threads: { singleThread, subThread, unknownThread } };
}
