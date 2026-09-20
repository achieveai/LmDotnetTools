// compaction.mjs — single-call Playwright check of just-in-time compaction in the chat UI (#721, spec 679
// §5, §7.2): the policy decision in the Context panel, the "Context compacted" divider (live, after a
// reload, and after the recall turn that follows the reload), RecallConversation reading rows from behind the
// divider, and a failed summary that leaves the transcript untouched. Run in ONE call:
//
//   browser_run_code_unsafe({ filename: "samples/LmStreaming.Sample/playwright-scripts/compaction.mjs" })
//
// Returns { pass, failures, steps, shots, threads, diagnostics }. Asserts only deterministic, browser-observable state
// (data-testid + /api reads). Prompts and the host profile: PromptExamples.md "Compaction (#721) UI tests".
//
// The host must run the test profile from PromptExamples.md (window 18,000, output 1,024). One host mode per
// run, so set HOST_MODE to match the host:
//   * 'Compact' — M2 (checkpoint + divider live/reload/replay), M3 (recall), M4 (summary failure),
//                 M5 (manual Compact now with a focus prompt), M6 (Compact now at phone width)
//   * 'Shadow'  — M1 (shadow decisions, no divider)
// RUN_AUTO / RUN_MANUAL pick the Compact-mode groups: M2–M4 take minutes; M5–M6 alone take under one.
// M5–M6 need the host to advertise `manualCompaction` on GET /api/conversations/capabilities.
async (page) => {
  const BASE = 'http://localhost:5173';
  const HOST_MODE = 'Compact';
  const MODE_ID = 'default'; // General Assistant: small tool list, so the band math in PromptExamples.md holds
  const RUN_AUTO = true; // M2, M3, M4
  const RUN_MANUAL = true; // M5, M6
  // The first turn at the hard band on the 18,000-token profile. Turn n ≈ 10,138 + (n − 1) × 1,346 estimated
  // tokens (the fixed prefix includes the tool schemas); hard fires at tokens + 1,024 ≥ 16,200. See PromptExamples.md.
  const HARD_TURN = 5;
  // Relative, under the gitignored `.logs/`: Playwright resolves it against the MCP server's cwd (normally the
  // repo or worktree root). Where that cwd is elsewhere, a failed write shows up in `diagnostics` with its path.
  const SHOT_DIR = '.logs/compaction';
  const shots = {};
  const steps = [];
  const record = (name, pass, detail) => steps.push({ name, pass, detail });
  const tid = (id) => page.locator(`[data-testid="${id}"]`);
  const chain = (steps) => `<|instruction_start|>${JSON.stringify({ instruction_chain: steps })}<|instruction_end|>`;

  // Prompts — kept byte-identical to PromptExamples.md.
  const SUMMARY = JSON.stringify({
    goals: ['Exercise conversation compaction from the sample UI'],
    narrative: 'The user sent filler turns to fill the context window; the assistant answered each with long filler text.',
  });
  const SEED = `Compaction seed. ${chain([{ id: 'compaction-seed', id_message: 'Seed with summary', messages: [{ text: SUMMARY }, { text_message: { length: 800 } }] }])}`;
  const FILL = `Compaction filler. ${chain([{ id: 'compaction-fill', id_message: 'Filler', messages: [{ text_message: { length: 800 } }] }])}`;
  const RECALL = `Compaction recall. ${chain([
    { id: 'compaction-recall', id_message: 'Recall', messages: [{ tool_call: [{ name: 'RecallConversation', args: { query: 'Compaction seed', limit: 3 } }] }] },
    { id: 'compaction-recall-done', id_message: 'Recall done', messages: [{ text: 'Recalled the seed turn from before the checkpoint.' }] },
  ])}`;
  /** The focus prompt M5 types into the Compact now form. */
  const FOCUS = 'keep the API design decisions';

  const api = (path) =>
    page.evaluate(async (p) => {
      const r = await fetch(p);
      return r.ok ? await r.json() : { status: r.status };
    }, path);
  const threadIds = async () => {
    const body = await api('/api/conversations');
    return (body.conversations ?? body ?? []).map((c) => c.threadId);
  };
  const decisionOf = async (threadId) => {
    const report = await api(`/api/conversations/${threadId}/context`);
    const agent = report.agents?.[0] ?? {};
    const d = agent.observation?.decision;
    return {
      decision: d ? `${d.decision}${d.reason ? `/${d.reason}` : ''}` : null,
      // The size the policy measured (the raw request), and the size of what was actually sent.
      decisionTokens: d?.tokens ?? null,
      tokens: agent.observation?.estimated_input_tokens ?? null,
      state: agent.compaction?.state ?? null,
      checkpointId: agent.compaction?.checkpointId ?? null,
      reason: agent.compaction?.reason ?? null,
    };
  };
  const dividers = () => page.locator('[data-testid="notification-pill"][data-notify-kind="compaction"]');
  /** How many user bubbles precede each divider in document order — proves placement, not just presence. */
  const dividerPlacement = () =>
    page.evaluate(() => {
      const users = [...document.querySelectorAll('[data-testid="user-message-group"]')];
      return [...document.querySelectorAll('[data-testid="notification-pill"][data-notify-kind="compaction"]')].map((d) => ({
        usersBefore: users.filter((u) => u.compareDocumentPosition(d) & Node.DOCUMENT_POSITION_FOLLOWING).length,
        label: d.querySelector('[data-testid="notification-label"]')?.textContent?.trim() ?? null,
        checkpointId: d.getAttribute('data-checkpoint-id'),
      }));
    });

  const selectProvider = async (providerId, re) => {
    for (let attempt = 0; attempt < 5; attempt++) {
      try {
        await tid('provider-selector-button').click();
        const opt = tid(`provider-option-${providerId}`);
        await opt.waitFor({ state: 'visible', timeout: 8000 });
        await page.waitForTimeout(350);
        await opt.click({ timeout: 5000 });
        if (re.test((await tid('provider-selector-button').textContent()) ?? '')) return true;
      } catch {
        await page.keyboard.press('Escape').catch(() => {});
        await page.waitForTimeout(300);
      }
    }
    return false;
  };
  /**
   * The browser remembers the last mode. A sandbox mode (e.g. Workspace Agent) counts ~21,000 tokens of prompt
   * and tool schemas on turn 1, over the 18,000 window, so every band in the table is wrong. Pin the default mode.
   */
  const selectMode = async (modeId) => {
    for (let attempt = 0; attempt < 5; attempt++) {
      try {
        await tid('mode-selector-button').click();
        const opt = tid(`mode-option-${modeId}`);
        await opt.waitFor({ state: 'visible', timeout: 8000 });
        const name = ((await opt.textContent()) ?? '').trim();
        await opt.click({ timeout: 5000 });
        await page.waitForTimeout(300);
        const button = ((await tid('mode-selector-button').textContent()) ?? '').trim();
        const firstWord = name.split(/\s+/)[0];
        if (firstWord && button.includes(firstWord)) return { ok: true, name, button };
      } catch {
        await page.keyboard.press('Escape').catch(() => {});
        await page.waitForTimeout(300);
      }
    }
    return { ok: false, button: ((await tid('mode-selector-button').textContent().catch(() => '')) ?? '').trim() };
  };
  const freshChat = async () => {
    for (let i = 0; i < 5; i++) {
      await page.locator('[data-testid="sidebar-new-chat"]').click().catch(() => {});
      await page.waitForTimeout(500);
      if ((await tid('user-message-group').count()) === 0) return true;
    }
    return false;
  };
  /**
   * Sends one turn and waits on the SERVER's run state (not the UI) until it has run and settled.
   * `onStreaming` is polled while the run is in progress, for assertions that must hold mid-run.
   */
  const sendTurn = async (text, threadId, onStreaming, timeout = 120000) => {
    const usersBefore = await tid('user-message-group').count();
    await tid('chat-input-textarea').fill(text);
    await tid('send-button').click();
    const deadline = Date.now() + timeout;
    while (Date.now() < deadline && (await tid('user-message-group').count()) === usersBefore) await page.waitForTimeout(100);
    let id = threadId;
    for (let i = 0; !id && i < 50; i++) {
      id = (await threadIds()).find((x) => !knownThreads.has(x)) ?? null;
      if (!id) await page.waitForTimeout(200);
    }
    let seenRunning = false;
    const started = Date.now();
    while (Date.now() < deadline) {
      const state = id ? await api(`/api/conversations/${id}/run-state`) : { isInProgress: true };
      if (state.isInProgress) {
        seenRunning = true;
        if (onStreaming) await onStreaming();
      } else if (seenRunning || Date.now() - started > 5000) {
        break;
      }
      await page.waitForTimeout(250);
    }
    await tid('send-button').waitFor({ state: 'visible', timeout: 30000 }).catch(() => {});
    return id;
  };
  // A failed screenshot goes to `diagnostics`, not `steps`: it is reported, never swallowed, but a missing
  // directory is not a product defect, so it cannot turn `pass` red.
  const diagnostics = [];
  const shot = async (name, prefix = 'pw721') => {
    const path = `${SHOT_DIR}/${prefix}-${name}.png`;
    try {
      await page.screenshot({ path, fullPage: false });
      shots[name] = path;
    } catch (e) {
      diagnostics.push({ name: `screenshot ${name}`, path, error: String((e && e.message) || e) });
    }
  };
  const openChat = async (threadId) => {
    await page.goto(`${BASE}/?threadId=${threadId}`);
    await tid('chat-input-textarea').waitFor({ timeout: 20000 });
    await tid('user-message-group').first().waitFor({ timeout: 20000 }).catch(() => {});
    await page.waitForTimeout(800);
  };
  /** Starts a fresh test-anthropic chat and sends `plan` turns, recording the decision after each. */
  const runPlan = async (label, plan, perTurn) => {
    await page.goto(BASE);
    await tid('chat-input-textarea').waitFor({ timeout: 20000 });
    await page.waitForTimeout(1000);
    const fresh = await freshChat();
    const provider = await selectProvider('test-anthropic', /Anthropic/i);
    const mode = await selectMode(MODE_ID);
    record(`${label}: fresh test-anthropic chat in the ${MODE_ID} mode`, fresh && provider && mode.ok, { fresh, provider, mode });
    knownThreads = new Set(await threadIds());
    let threadId = null;
    const decisions = [];
    for (let turn = 1; turn <= plan.length; turn++) {
      threadId = await sendTurn(plan[turn - 1], threadId, perTurn?.streaming?.(turn));
      decisions.push({ turn, ...(await decisionOf(threadId)) });
      await perTurn?.after?.(turn, threadId);
    }
    return { threadId, decisions };
  };
  let knownThreads = new Set();
  const threads = {};

  /**
   * Polls `probe` until `done(value)` holds or the timeout passes. Returns `{ ok, value }` so a timeout is
   * recorded as a failed step by the caller, never read as a pass.
   */
  const pollUntil = async (probe, done, timeout = 60000, interval = 250) => {
    const deadline = Date.now() + timeout;
    let value = await probe();
    while (!done(value) && Date.now() < deadline) {
      await page.waitForTimeout(interval);
      value = await probe();
    }
    return { ok: done(value), value };
  };
  /** Every POST to /api/conversations/{id}/compaction, with the body sent and the answer received. */
  const compactCalls = [];
  // The page outlives this script: a listener left attached would run on every later script's responses.
  const onCompactResponse = async (resp) => {
    const req = resp.request();
    // No `URL` global in the MCP runner's sandbox: match the path with the query string cut off.
    if (req.method() !== 'POST' || !/\/api\/conversations\/[^/?]+\/compaction$/.test(resp.url().split('?')[0])) return;
    const entry = { url: resp.url(), requestBody: req.postData(), status: resp.status(), body: null, done: false };
    compactCalls.push(entry);
    try {
      entry.body = await resp.json();
    } catch {
      entry.body = null;
    }
    entry.done = true;
  };
  page.on('response', onCompactResponse);
  const nthCompactCall = (n, timeout = 15000) => pollUntil(async () => compactCalls[n] ?? null, (c) => !!c?.done, timeout, 100);
  /**
   * Records every distinct (status phase, status text, button disabled) triple the Compact now control goes
   * through. The mock summarizer is fast, so "Compacting…" can be on screen for less than a poll interval;
   * a MutationObserver sees it anyway.
   */
  const watchCompactControl = () =>
    page.evaluate(() => {
      window.__compactObsStop?.();
      const obs = (window.__compactObs = []);
      const sample = () => {
        const s = document.querySelector('[data-testid="compact-status"]');
        const b = document.querySelector('[data-testid="compact-now-button"]');
        const entry = {
          phase: s?.getAttribute('data-phase') ?? null,
          text: s?.textContent?.trim() ?? null,
          disabled: b ? b.disabled : null,
        };
        const last = obs[obs.length - 1];
        if (!last || last.phase !== entry.phase || last.text !== entry.text || last.disabled !== entry.disabled) obs.push(entry);
      };
      sample();
      const mo = new MutationObserver(sample);
      mo.observe(document.body, { subtree: true, childList: true, attributes: true, characterData: true });
      window.__compactObsStop = () => mo.disconnect();
    });
  const compactObservations = () => page.evaluate(() => window.__compactObs ?? []);
  /** Waits for the server to finish any run on the thread (run-state), not for the UI to look idle. */
  const waitRunIdle = (threadId, timeout = 60000) =>
    pollUntil(() => api(`/api/conversations/${threadId}/run-state`), (s) => s && s.isInProgress === false, timeout);

  try {
    await page.setViewportSize({ width: 1280, height: 900 });
    // `api()` fetches relative paths from the page, so the page must be on the app before the first read.
    if (!page.url().startsWith('http')) {
      await page.goto(BASE);
      await tid('chat-input-textarea').waitFor({ timeout: 20000 });
    }

    if (HOST_MODE === 'Compact' && RUN_AUTO) {
      // ---- M2. hard-band checkpoint: divider live, after reload, and after a live turn post-reload ----
      let liveWhileStreaming = false;
      const m2Plan = [SEED, ...Array(HARD_TURN - 1).fill(FILL)];
      const m2 = await runPlan('M2', m2Plan, {
        streaming: (turn) => (turn === HARD_TURN ? async () => { if ((await dividers().count()) > 0) liveWhileStreaming = true; } : null),
      });
      threads.m2 = m2.threadId;
      const t6 = m2.decisions[HARD_TURN - 1] ?? {};
      record('M2: turns 1-2 take no action', m2.decisions.slice(0, 2).every((d) => d.decision === 'no_action'), m2.decisions);
      record(`M2: turns before ${HARD_TURN} do not compact`,
        m2.decisions.slice(0, HARD_TURN - 1).every((d) => d.state !== 'Active' && !/^(compact|failed)/.test(d.decision ?? '')),
        m2.decisions);
      record(`M2: turn ${HARD_TURN} compacts at the hard band and the checkpoint is Active`,
        t6.decision === 'compact/hard' && t6.state === 'Active' && /^cp-/.test(t6.checkpointId ?? ''), t6);
      const live = await dividerPlacement();
      record('M2: exactly one divider, live, without a reload', live.length === 1, live);
      record(`M2: the divider appeared while turn ${HARD_TURN} was still running`, liveWhileStreaming, { liveWhileStreaming });
      record(`M2: the divider sits after user bubble ${HARD_TURN} and names the active checkpoint`,
        live[0]?.usersBefore === HARD_TURN && live[0]?.checkpointId === t6.checkpointId && /^\d+ rows · ~[\d,]+ tokens saved$/.test(live[0]?.label ?? ''),
        { live, checkpointId: t6.checkpointId });
      await dividers().first().locator('[data-testid="notification-label"]').click();
      const body = (await tid('notification-body').first().textContent().catch(() => '')) ?? '';
      record('M2: expanding the divider shows the goal, narrative and checkpoint id',
        body.includes('Exercise conversation compaction from the sample UI') && body.includes('## What happened') && body.includes(t6.checkpointId ?? '<none>'),
        { body: body.slice(0, 400) });
      await shot('m2-live');

      await tid('context-panel-toggle').click().catch(() => {});
      await tid('context-row-details-toggle').first().click().catch(() => {});
      const panel = {
        compaction: (await tid('context-compaction').first().textContent().catch(() => null))?.trim(),
        decision: (await tid('context-decision').first().textContent().catch(() => null))?.trim(),
      };
      record('M2: the panel reads Compacted with a readable decision (not a raw wire value)',
        panel.compaction === 'Compacted' && !!panel.decision && panel.decision !== 'No decision yet' && !/_/.test(panel.decision), panel);
      await shot('m2-panel');

      await openChat(m2.threadId);
      const reloaded = await dividerPlacement();
      record('M2: after reload still exactly one divider, same place, same checkpoint',
        reloaded.length === 1 && reloaded[0].usersBefore === HARD_TURN && reloaded[0].checkpointId === t6.checkpointId, reloaded);
      await shot('m2-reload');

      // ---- M3. the live turn after the reload is the recall ----
      // It must directly follow the hard turn: a second compaction finds only filler rows (no JSON summary).
      // A live turn after the reload replays nothing that may add a second divider (the distinguishing case).
      await sendTurn(RECALL, m2.threadId);
      const t7 = await decisionOf(m2.threadId);
      const afterReplay = await dividerPlacement();
      record('M2: a live turn after reload keeps one divider; the next decision is cooldown',
        afterReplay.length === 1 && t7.decision === 'skipped/cooldown' && t7.state === 'Active', { afterReplay, t7 });
      record(`M2: turn ${HARD_TURN} sent a smaller view than the raw request it measured, and the recall turn stays below that raw size`,
        t6.tokens < t6.decisionTokens && t7.decisionTokens < t6.decisionTokens,
        { t6Raw: t6.decisionTokens, t6Sent: t6.tokens, t7Raw: t7.decisionTokens });
      const recallPill = page.locator('[data-testid="tool-call-pill"][data-tool-name="RecallConversation"]');
      record('M3: a RecallConversation pill renders', (await recallPill.count()) >= 1, { count: await recallPill.count() });
      const messages = await api(`/api/conversations/${m2.threadId}/messages`);
      const resultRow = (Array.isArray(messages) ? messages : []).filter((m) => m.messageType === 'ToolCallResultMessage').pop();
      let recall = null;
      try {
        recall = JSON.parse(JSON.parse(resultRow.messageJson).result);
      } catch {
        recall = null;
      }
      record('M3: recall matched the seed row (seq 1) from before the boundary',
        !!recall && recall.matched >= 1 && recall.rows?.some((r) => r.seq === 1 && r.seq <= recall.boundary_seq && r.text.includes('Compaction seed')),
        recall && { boundary: recall.boundary_seq, matched: recall.matched, seqs: recall.rows?.map((r) => r.seq) });
      const texts = await tid('assistant-text').allTextContents();
      record('M3: the scripted follow-up after recall rendered', texts.some((t) => t.includes('Recalled the seed turn from before the checkpoint.')), { last: texts.at(-1)?.slice(0, 80) });
      record('M3: still exactly one divider', (await dividers().count()) === 1, {});
      await shot('m3-recall');

      // ---- M4. a summary that is not JSON fails; no divider; over the window the run is refused ----
      // Two turns past the hard band: the last one is over the usable window with nothing compacted.
      const m4Turns = HARD_TURN + 2;
      const m4 = await runPlan('M4', Array(m4Turns).fill(FILL));
      threads.m4 = m4.threadId;
      const f6 = m4.decisions[HARD_TURN - 1] ?? {};
      const f7 = m4.decisions[m4Turns - 1] ?? {};
      record(`M4: turn ${HARD_TURN} fails with summary_call_failed and the checkpoint is Rejected`,
        f6.decision === 'failed/summary_call_failed' && f6.state === 'Rejected' && f6.reason === 'summary_call_failed', f6);
      record('M4: no divider is drawn for a failed checkpoint', (await dividers().count()) === 0, {});
      const m4Messages = await api(`/api/conversations/${m4.threadId}/messages`);
      const kinds = (Array.isArray(m4Messages) ? m4Messages : []).map((m) => `${m.messageType}:${m.role}`);
      const lastUser = kinds.lastIndexOf('TextMessage:User');
      const repliesAfterLastUser = kinds.slice(lastUser + 1).filter((k) => k.endsWith(':Assistant') && k.startsWith('TextMessage')).length;
      const userTurns = kinds.filter((k) => k === 'TextMessage:User').length;
      record(`M4: turn ${HARD_TURN} still replied; turn ${m4Turns} (over the usable window) got no reply`,
        userTurns === m4Turns && repliesAfterLastUser === 0 &&
          kinds.slice(0, lastUser).filter((k) => k === 'TextMessage:Assistant').length >= HARD_TURN,
        { userTurns, repliesAfterLastUser, decisions: m4.decisions, last: f7 });
      await shot('m4-failed');
    }

    if (HOST_MODE === 'Compact' && RUN_MANUAL) {
      // ---- M5. manual compaction: Compact now + focus, below the automatic thresholds ----
      const caps = await api('/api/conversations/capabilities');
      record('M5: the host advertises manualCompaction', caps?.manualCompaction === true, caps);
      if (caps?.manualCompaction !== true) {
        record('M5/M6: skipped — no manualCompaction capability on this host', false, {});
      } else {
        // A second conversation to switch to; its panel stays mounted, so a cleared status is not just an unmount.
        const other = await runPlan('M5 other thread', [FILL]);
        threads.m5other = other.threadId;

        // Seed (JSON summary for the mock summarizer) + 1 filler ≈ 11,500 request tokens: under the compact
        // bands (13,581 economic, 15,176 hard). The tail keeps the filler turn, so the cut covers the seed.
        const m5 = await runPlan('M5', [SEED, FILL]);
        threads.m5 = m5.threadId;
        record('M5: two turns stay below the automatic thresholds (no compaction yet)',
          m5.decisions.every((d) => d.state !== 'Active' && !/^(compact|failed)/.test(d.decision ?? '')) &&
            (await dividers().count()) === 0,
          m5.decisions);
        const idleBefore = await waitRunIdle(m5.threadId);
        record('M5: the run is idle on the server before Compact now', idleBefore.ok, idleBefore.value);

        const button = tid('compact-now-button');
        const buttonReady = await button.waitFor({ state: 'visible', timeout: 15000 }).then(() => true).catch(() => false);
        record('M5: the capability-gated Compact now button is visible and enabled',
          buttonReady && (await button.isEnabled()), { buttonReady });

        await button.click();
        const formOpen = await tid('compact-form').waitFor({ state: 'visible', timeout: 5000 }).then(() => true).catch(() => false);
        const focused = await page.evaluate(() => document.activeElement?.getAttribute('data-testid'));
        record('M5: the button opens the focus form with the input focused',
          formOpen && focused === 'compact-focus-input' && (await button.getAttribute('aria-expanded')) === 'true',
          { formOpen, focused });
        await shot('m5-form', 'compaction');

        await watchCompactControl();
        await tid('compact-focus-input').fill(FOCUS);
        await page.keyboard.press('Enter');

        const first = await nthCompactCall(0);
        let sentBody = null;
        try {
          sentBody = JSON.parse(first.value?.requestBody ?? 'null');
        } catch {
          sentBody = null;
        }
        record('M5: Enter POSTs the focus to this thread and the host answers 202 with a request id',
          first.ok && first.value.url.includes(`/api/conversations/${encodeURIComponent(m5.threadId)}/compaction`) &&
            sentBody?.focus === FOCUS && Object.keys(sentBody).length === 1 &&
            first.value.status === 202 && typeof first.value.body?.requestId === 'string' &&
            ['queued', 'running'].includes(first.value.body?.status),
          first.value);

        const applied = await pollUntil(
          () => decisionOf(m5.threadId),
          (d) => d.state === 'Active' && /^cp-/.test(d.checkpointId ?? ''),
          90000
        );
        record('M5: the context report reaches compaction Active with a checkpoint id', applied.ok, applied.value);
        await waitRunIdle(m5.threadId);

        const sawApplied = await pollUntil(compactObservations, (o) => o.some((e) => e.phase === 'applied'), 15000, 100);
        const obs = sawApplied.value;
        record('M5: the control showed Compacting… with the button disabled while the compaction was under way',
          obs.some((e) => e.text === 'Compacting…' && e.disabled === true), obs);
        record('M5: the control reached applied ("Conversation compacted.")',
          sawApplied.ok && obs.some((e) => e.phase === 'applied' && e.text === 'Conversation compacted.'), obs);
        await page.evaluate(() => window.__compactObsStop?.());

        const liveDivider = await pollUntil(dividerPlacement, (d) => d.length === 1, 15000);
        record('M5: exactly one divider appears live, naming the new checkpoint',
          liveDivider.ok && liveDivider.value[0]?.checkpointId === applied.value.checkpointId,
          { dividers: liveDivider.value, checkpointId: applied.value.checkpointId });
        if (liveDivider.ok) {
          await dividers().first().locator('[data-testid="notification-label"]').click();
          const liveBody = (await tid('notification-body').first().textContent().catch(() => '')) ?? '';
          record('M5: the expanded divider shows the focus under ## Focus', liveBody.includes(`## Focus\n${FOCUS}`),
            { body: liveBody.slice(0, 300) });
        }
        await shot('m5-applied', 'compaction');

        // Right after the cut the gauge must show the compacted size, not the pre-cut decision's tokens.
        const reportAfter = await api(`/api/conversations/${m5.threadId}/context`);
        const rootAfter = (reportAfter.agents ?? []).find((a) => a.agentId === 'root') ?? {};
        const activeCp = rootAfter.compaction?.activeCheckpoint ?? null;
        const lastDecision = rootAfter.compaction?.lastDecision ?? null;
        record('M5: /context carries activeCheckpoint for the new checkpoint, and the ordinals line up',
          activeCp?.checkpointId === applied.value.checkpointId &&
            typeof activeCp?.estimatedTokensAfter === 'number' && typeof activeCp?.generationOrdinal === 'number' &&
            (lastDecision === null || lastDecision.generationOrdinal <= activeCp.generationOrdinal),
          { activeCheckpoint: activeCp, lastDecision: lastDecision && { ordinal: lastDecision.generationOrdinal, decision: lastDecision.decision } });
        const panelAfter = await pollUntil(
          async () => ((await tid('context-panel-summary').textContent().catch(() => '')) ?? '').trim(),
          (text) => text.includes('after compaction'),
          15000
        );
        await tid('context-panel-toggle').click().catch(() => {});
        await tid('context-row-details-toggle').first().click().catch(() => {});
        await page.waitForTimeout(400);
        const capacityAfter = ((await tid('context-capacity').first().textContent().catch(() => '')) ?? '').trim();
        record('M5: the panel shows the after-compaction figure right after the cut',
          panelAfter.ok && capacityAfter.includes('after compaction'),
          { summary: panelAfter.value, capacity: capacityAfter, summaryTitle: await tid('context-panel-summary').getAttribute('title').catch(() => null) });
        await shot('m5-panel-after', 'compaction');
        await tid('context-panel-toggle').click().catch(() => {});

        // Reload: the focus must come from the persisted checkpoint row, not from client memory.
        await openChat(m5.threadId);
        const reloadedDividers = await dividerPlacement();
        let reloadedBody = '';
        if (reloadedDividers.length === 1) {
          await dividers().first().locator('[data-testid="notification-label"]').click();
          reloadedBody = (await tid('notification-body').first().textContent().catch(() => '')) ?? '';
        }
        record('M5: after reload one divider, same checkpoint, focus still shown',
          reloadedDividers.length === 1 && reloadedDividers[0].checkpointId === applied.value.checkpointId &&
            reloadedBody.includes(`## Focus\n${FOCUS}`),
          { reloadedDividers, body: reloadedBody.slice(0, 300) });

        // Nothing new since the checkpoint: a second request is refused.
        const again = tid('compact-now-button');
        const againReady = await pollUntil(
          async () => (await again.count()) > 0 && (await again.isEnabled()),
          (v) => v === true,
          15000
        );
        record('M5: the button is enabled again after the compaction applied', againReady.ok, {});
        await again.click();
        await tid('compact-submit-button').click();
        const second = await nthCompactCall(1);
        record('M5: an empty-focus request sends {} and is refused 409 nothing_to_compact',
          second.ok && second.value.requestBody === '{}' && second.value.status === 409 && second.value.body?.reason === 'nothing_to_compact',
          second.value);
        const refusedShown = await pollUntil(
          async () => ({
            text: (await tid('compact-status').textContent().catch(() => null))?.trim() ?? null,
            enabled: (await tid('compact-now-button').count()) > 0 && (await tid('compact-now-button').isEnabled()),
          }),
          (v) => v.text === 'Nothing to compact yet.' && v.enabled,
          10000
        );
        record('M5: the refusal reads "Nothing to compact yet." and the button is enabled', refusedShown.ok, refusedShown.value);
        record('M5: the refusal drew no second divider', (await dividers().count()) === 1, {});
        await shot('m5-refused', 'compaction');

        // Conversation switch: the refusal line belongs to thread A only.
        const switchTo = async (threadId, expectedUsers) => {
          await page.locator(`[data-testid="conversation-item"][data-thread-id="${threadId}"]`).first().click();
          return pollUntil(
            async () => ({
              users: await tid('user-message-group').count(),
              panel: await tid('context-panel').count(),
              status: await tid('compact-status').count(),
            }),
            (v) => v.users === expectedUsers && v.panel === 1,
            20000
          );
        };
        const onOther = await switchTo(other.threadId, 1);
        record('M5: switching conversation clears compact-status while the panel stays mounted',
          onOther.ok && onOther.value.status === 0, onOther.value);
        const backOnA = await switchTo(m5.threadId, 2);
        record('M5: switching back does not resurrect the old status', backOnA.ok && backOnA.value.status === 0, backOnA.value);
        await shot('m5-switched', 'compaction');

        // ---- M6. phone width: header button and the open form fit a 400px viewport ----
        await page.setViewportSize({ width: 400, height: 860 });
        await openChat(m5.threadId);
        const phoneButton = tid('compact-now-button');
        const phoneReady = await phoneButton.waitFor({ state: 'visible', timeout: 15000 }).then(() => true).catch(() => false);
        if (phoneReady) {
          // A click the narrow layout intercepts (e.g. an overlaying sidebar) must fail the fit steps, not throw.
          await phoneButton.click({ timeout: 5000 }).catch(() => {});
          await tid('compact-form').waitFor({ state: 'visible', timeout: 5000 }).catch(() => {});
          await tid('compact-focus-input').fill(FOCUS, { timeout: 5000 }).catch(() => {});
        }
        const fit = await page.evaluate(() => {
          const rect = (id) => {
            const e = document.querySelector(`[data-testid="${id}"]`);
            if (!e) return null;
            const r = e.getBoundingClientRect();
            return { left: Math.round(r.left), right: Math.round(r.right), width: Math.round(r.width) };
          };
          return {
            innerWidth: window.innerWidth,
            scrollWidth: document.documentElement.scrollWidth,
            panel: rect('context-panel'),
            toggle: rect('context-panel-toggle'),
            button: rect('compact-now-button'),
            form: rect('compact-form'),
            input: rect('compact-focus-input'),
            submit: rect('compact-submit-button'),
            cancel: rect('compact-cancel-button'),
          };
        });
        const inside = (r) => !!r && r.left >= 0 && r.right <= fit.innerWidth && r.width > 0;
        record('M6: at 400px the header button and the open form render', phoneReady && !!fit.form, fit);
        record('M6: at 400px there is no horizontal scroll (scrollWidth <= innerWidth)', fit.scrollWidth <= fit.innerWidth, fit);
        record('M6: at 400px the button, input, Compact and Cancel all sit inside the viewport',
          [fit.button, fit.input, fit.submit, fit.cancel].every(inside), fit);
        await shot('m6-phone-form', 'compaction');
        await page.keyboard.press('Escape').catch(() => {});
        await page.setViewportSize({ width: 1280, height: 900 });
      }
    }

    if (HOST_MODE !== 'Compact') {
      // ---- M1. shadow: decisions are recorded, nothing is applied ----
      // Not re-run since the 18,000-token profile; the plan follows the same band table as M2.
      const m1 = await runPlan('M1', [SEED, ...Array(HARD_TURN - 1).fill(FILL)]);
      threads.m1 = m1.threadId;
      const d = m1.decisions;
      const hard = d[HARD_TURN - 1];
      record(`M1: turn ${HARD_TURN} is a shadow decision at the hard band`, hard?.decision === 'shadow/hard', d);
      record('M1: the shadow build is recorded as Rejected/shadow, never Active',
        d.every((x) => x.state !== 'Active') && hard?.state === 'Rejected' && hard?.reason === 'shadow', d);
      record('M1: request sizes keep growing (the raw history is sent)',
        d.every((x, i) => i === 0 || (x.tokens ?? 0) > (d[i - 1].tokens ?? 0)), d.map((x) => x.tokens));
      record('M1: no divider', (await dividers().count()) === 0, {});
      await shot('m1-shadow');
    }
  } catch (e) {
    record('exception', false, String((e && e.stack) || e));
    await shot('exception');
  }

  page.off('response', onCompactResponse);
  await page.evaluate(() => window.__compactObsStop?.()).catch(() => {});
  const failures = steps.filter((s) => !s.pass).map((s) => s.name);
  return { pass: failures.length === 0, failures, steps, shots, threads, diagnostics }
}
