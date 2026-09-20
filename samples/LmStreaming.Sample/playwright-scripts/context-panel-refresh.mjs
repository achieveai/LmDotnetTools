// context-panel-refresh.mjs — single-call Playwright check of the two context-panel refresh fixes.
// Run in ONE call:
//
//   browser_run_code_unsafe({ filename: "samples/LmStreaming.Sample/playwright-scripts/context-panel-refresh.mjs" })
//
// Returns { pass, failures, steps, requests }. Everything asserted here is browser-observable:
// the exact GET /api/conversations/{threadId}/context traffic (start/end timestamps, taken from
// page.on('request') / ('requestfinished') / ('requestfailed')) plus data-testid state.
//
// F-003 — revealing the Developer view is itself a refresh signal.
//   `ChatLayout.vue` throttles the usage-driven re-read behind `showDeveloperDiagnostics`, so usage
//   that moved while the panel was hidden is DROPPED. A watch on `showDeveloperDiagnostics` bumps
//   `contextUsageTick` on the hidden->visible edge. Proof: run a whole turn in Consumer view, wait
//   for the /context traffic to go quiet, CLEAR the recorded list, then switch to Developer — at
//   least one authoritative GET must fire inside that reveal window.
//
// F-002 — at most ONE in-flight GET /context per mounted panel.
//   `useContextReport.hydrate` is single-flight with one trailing refresh that coalesces everything
//   asked while a read runs. Proof: across the WHOLE session no two recorded /context requests ever
//   overlap (each starts only after the previous one finished). A deliberate burst of
//   Consumer<->Developer toggles, spaced well inside one measured request duration, is the
//   discriminator — without single-flight each hidden->visible edge would start its own read.
//
// Mock provider only (no spend): `test-anthropic` runs as `claude-sonnet-4-5-20250929`, which is
// priced with a 200,000-token window and persists a usage row, so the run really does move usage
// while the panel is hidden. Prompt: PromptExamples.md -> "Context / cost panel (#685) UI tests".
async (page) => {
  const BASE = 'http://localhost:5050';
  const PROVIDER = 'test-anthropic';
  const PROMPT =
    '<|instruction_start|>{"instruction_chain":[{"id":"t1","id_message":"Say hello","messages":[{"text":"Hello from the context panel check."}]}]}<|instruction_end|>';

  const steps = [];
  const record = (name, pass, detail) => steps.push({ name, pass, detail });
  const tid = (id) => page.locator(`[data-testid="${id}"]`);

  // ---- /context traffic recorder ---------------------------------------------------------------
  const CTX = /\/api\/conversations\/[^/]+\/context(\?|$)/;
  const t0 = Date.now();
  const ms = () => Date.now() - t0;
  /** Every GET /context seen on this page, in start order. `end === null` means still in flight. */
  let reqs = [];
  const byRequest = new Map();
  page.on('request', (r) => {
    if (r.method() !== 'GET' || !CTX.test(r.url())) return;
    const rec = { url: r.url(), threadId: r.url().split('/api/conversations/')[1]?.split('/')[0] ?? null, start: ms(), end: null, outcome: null };
    reqs.push(rec);
    byRequest.set(r, rec);
  });
  const close = (outcome) => (r) => {
    const rec = byRequest.get(r);
    if (rec && rec.end === null) {
      rec.end = ms();
      rec.outcome = outcome;
    }
  };
  page.on('requestfinished', close('finished'));
  page.on('requestfailed', close('failed'));

  const all = [];
  /** Moves what has been recorded so far into the archive and starts a fresh window. */
  const cut = () => {
    all.push(...reqs);
    const taken = reqs;
    reqs = [];
    return taken;
  };
  /** Waits until nothing new was requested for `quietMs` and nothing is still in flight. */
  const waitCtxQuiet = async (quietMs = 2000, timeout = 30000) => {
    const deadline = Date.now() + timeout;
    let lastCount = reqs.length;
    let lastChange = Date.now();
    while (Date.now() < deadline) {
      await page.waitForTimeout(150);
      if (reqs.length !== lastCount) {
        lastCount = reqs.length;
        lastChange = Date.now();
        continue;
      }
      const inFlight = reqs.some((r) => r.end === null);
      if (!inFlight && Date.now() - lastChange >= quietMs) return true;
      if (inFlight) lastChange = Date.now();
    }
    return false;
  };
  /** Non-overlap check: sorted by start, each request begins only after the previous one ended. */
  const overlaps = (list) => {
    const sorted = [...list].sort((a, b) => a.start - b.start);
    const bad = [];
    for (let i = 1; i < sorted.length; i++) {
      const prev = sorted[i - 1];
      const cur = sorted[i];
      const prevEnd = prev.end === null ? Number.POSITIVE_INFINITY : prev.end;
      if (cur.start < prevEnd) bad.push({ first: prev, second: cur, overlapMs: prevEnd - cur.start });
    }
    return bad;
  };

  // ---- UI helpers (same shape as provider-switch.mjs / context-cost-panel.mjs) -------------------
  const send = async (text) => {
    await tid('chat-input-textarea').fill(text);
    await tid('send-button').click();
  };
  const waitRunStartThenIdle = async (timeout = 60000) => {
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
  const isEmptyChat = async () =>
    (await page.locator('[data-testid="user-message-group"]').count()) === 0 &&
    (await tid('usage-banner').count()) === 0;
  // `sidebar-new-chat`, not the older scripts' getByRole({ name: '+ New Chat' }) — the sidebar
  // button lost its "+" prefix and gained a testid, so the role lookup silently matches nothing.
  const freshChat = async () => {
    for (let i = 0; i < 5; i++) {
      await tid('sidebar-new-chat').click({ timeout: 10000 }).catch(() => {});
      await page.waitForTimeout(500);
      if (await isEmptyChat()) return true;
    }
    return false;
  };
  // The radio input itself is visually covered by its own <span> label, so click the LABEL — that
  // toggles the implicitly-associated input and fires `change`, which is what `v-model` listens to.
  const viewLabel = (which) => page.locator(`label:has(> [data-testid="view-preference-${which}"])`);
  const setView = async (which) => {
    await viewLabel(which).click({ timeout: 15000 });
    return page.evaluate(
      (w) => document.querySelector(`[data-testid="view-preference-${w}"]`)?.checked === true,
      which
    );
  };
  const viewState = () =>
    page.evaluate(() => ({
      consumer: document.querySelector('[data-testid="view-preference-consumer"]')?.checked === true,
      developer: document.querySelector('[data-testid="view-preference-developer"]')?.checked === true,
      // The panel's real testid is `context-panel` (ChatLayout's unit test stubs it as
      // `context-cost-panel`; that name exists only in the stub). It is `v-if`-mounted once the
      // conversation has started and `v-show`-hidden in Consumer view, so mounted != visible.
      panelMounted: !!document.querySelector('[data-testid="context-panel"]'),
      panelVisible: (document.querySelector('[data-testid="context-panel"]')?.getBoundingClientRect().height ?? 0) > 0,
    }));
  const threadIdFromUi = () =>
    page.evaluate(
      () => document.querySelector('[data-testid=conversation-item]')?.getAttribute('data-thread-id') ?? null
    );

  let threadId = null;
  let revealWindow = [];
  let burstWindow = [];
  let medianDuration = null;

  try {
    await page.setViewportSize({ width: 1280, height: 800 });
    await page.goto(BASE);
    await tid('chat-input-textarea').waitFor({ timeout: 25000 });
    await page.waitForTimeout(1200);

    // 1. Fresh chat, Consumer view, mock provider chosen BEFORE the first send (the thread locks to
    //    whatever provider is active then).
    const fresh = await freshChat();
    const consumerOk = await setView('consumer');
    const provider = await selectProvider(PROVIDER, /Anthropic/i);
    const pre = await viewState();
    // "Not started yet" is what matters, and the panel's own `v-if` is the honest witness for it:
    // no panel mounted == no conversation thread == nothing has been sent. `fresh` (the New Chat
    // button loop) is reported but not asserted; it is a convenience, not the precondition.
    record('setup: unstarted chat, Consumer view, mock provider selected', consumerOk && provider && !pre.developer && !pre.panelMounted, {
      fresh,
      consumerOk,
      provider,
      view: pre,
    });

    // 2. Run one turn entirely in Consumer view — usage moves while the panel is hidden.
    await send(PROMPT);
    const ran = await waitRunStartThenIdle();
    threadId = await threadIdFromUi();
    const duringRun = await viewState();
    // The panel is now mounted but HIDDEN — exactly the state whose usage F-003 says gets dropped.
    record('run: one mock turn reached idle with the panel mounted-but-hidden (Consumer view)',
      ran && !!threadId && !duringRun.developer && duringRun.panelMounted && !duringRun.panelVisible, {
      ran,
      threadId,
      view: duringRun,
    });

    // 3. Let the post-run /context traffic settle, then CUT the list so the next window is purely
    //    about the reveal.
    const quiet = await waitCtxQuiet(2000, 30000);
    const preReveal = cut();
    record('quiesce: /context traffic went quiet before the reveal', quiet, {
      quiet,
      requestsBeforeReveal: preReveal.length,
      lastEndMs: preReveal.length ? preReveal[preReveal.length - 1].end : null,
    });

    // 4. F-003 — reveal the Developer view; at least one authoritative GET must fire inside it.
    const devOk = await setView('developer');
    await waitCtxQuiet(1500, 20000);
    revealWindow = cut();
    const afterReveal = await viewState();
    const revealHits = revealWindow.filter((r) => r.threadId === threadId);
    record(
      'F-003: revealing the Developer view fires an authoritative GET /context for the open thread',
      devOk && afterReveal.developer && afterReveal.panelVisible && revealHits.length >= 1,
      {
        devOk,
        view: afterReveal,
        requestsInRevealWindow: revealWindow.length,
        forOpenThread: revealHits.length,
        timings: revealWindow.map((r) => ({ start: r.start, end: r.end, outcome: r.outcome, threadId: r.threadId })),
      }
    );
    record('F-003: the reveal was not noisy (a single re-read, not a storm)', revealWindow.length === 1, {
      count: revealWindow.length,
    });

    // 5. F-002 — burst of hidden->visible edges spaced well inside one measured request duration.
    //    Without single-flight each edge would start its own concurrent read.
    const durations = all
      .filter((r) => r.end !== null)
      .map((r) => r.end - r.start)
      .sort((a, b) => a - b);
    medianDuration = durations.length ? durations[Math.floor(durations.length / 2)] : null;
    const gap = Math.max(10, Math.floor((medianDuration ?? 120) / 4));
    for (let i = 0; i < 6; i++) {
      await viewLabel('consumer').click({ timeout: 15000 });
      await page.waitForTimeout(gap);
      await viewLabel('developer').click({ timeout: 15000 });
      await page.waitForTimeout(gap);
    }
    await waitCtxQuiet(2000, 30000);
    burstWindow = cut();
    record(
      'F-002 stress: six Consumer->Developer reveals produced at least one /context read',
      burstWindow.length >= 1,
      {
        toggleGapMs: gap,
        medianRequestMs: medianDuration,
        reads: burstWindow.length,
        timings: burstWindow.map((r) => ({ start: r.start, end: r.end, outcome: r.outcome })),
      }
    );
    // The burst is only a discriminator if a toggle could land inside a live request.
    record(
      'F-002 stress is meaningful (toggle gap < median request duration)',
      medianDuration !== null && gap < medianDuration,
      { toggleGapMs: gap, medianRequestMs: medianDuration }
    );

    // 6. F-002 — no two /context requests ever overlapped, across the WHOLE session.
    const bad = overlaps(all);
    record('F-002: no two GET /context requests were ever concurrent (single-flight holds)', bad.length === 0, {
      totalRequests: all.length,
      overlappingPairs: bad.length,
      worst: bad.slice(0, 3),
    });
    record('F-002: every recorded /context request completed', all.every((r) => r.end !== null && r.outcome === 'finished'), {
      unfinished: all.filter((r) => r.outcome !== 'finished').length,
    });
  } catch (e) {
    record('exception', false, String((e && e.stack) || e));
    cut();
  }

  const failures = steps.filter((s) => !s.pass).map((s) => s.name);
  return {
    pass: failures.length === 0,
    failures,
    steps,
    requests: {
      threadId,
      total: all.length,
      medianDurationMs: medianDuration,
      revealWindow,
      burstWindow,
      timeline: all.map((r) => ({ start: r.start, end: r.end, dur: r.end === null ? null : r.end - r.start, outcome: r.outcome })),
    },
  };
}
