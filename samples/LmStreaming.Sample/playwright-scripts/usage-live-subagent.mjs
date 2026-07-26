// usage-live-subagent.mjs — single-call Playwright check that the conversation-wide token-usage
// banner (#196) reflects SUB-AGENT usage *LIVE* — the banner Total climbs ABOVE the parent's own
// turns during/after an Agent delegation, NOT only after a reload. Run in ONE call:
//
//   browser_run_code_unsafe({ filename: "samples/LmStreaming.Sample/playwright-scripts/usage-live-subagent.mjs" })
//
// Returns { pass, failures, steps, bannerText, liveTotal, parentBaseline, aggregateTotal }.
// Assert only DETERMINISTIC, browser-observable state (data-testid + /api reads).
//
// Uses the MOCK provider `test-anthropic`, whose scripted SSE emits a fixed 100 input / 50 output
// tokens per generation. The parent runs TWO generations (Agent tool-call turn + final text turn) =>
// 300 on its own. The nested chain drives the sub-agent through TWO more generations (a `calculate`
// turn + a text turn) => +300. With the #196 fix the sub-agent's relayed usage is broadcast to the
// parent run's subscribers as a live ConversationUsageMessage frame, so the LIVE banner Total reaches
// 600 (In 400 / Out 200) WITHOUT a reload. Model: tests/.../Scenarios/UsageBannerTests.cs (InnerChain)
// + PromptExamples.md "Usage banner (#196) UI tests" / "Sub-Agent Delegation".
//
// Two determinism guards learned the hard way on a shared dev host:
//   * A brand-new chat has NO active conversation-item until the first send completes, so the current
//     thread id is resolved by DIFFING /api/conversations before vs after (never a ".active" fallback
//     to the first/pinned item — that silently reads an unrelated conversation).
//   * "stream idle" (stop hidden + send visible) is briefly TRUE in the instant after clicking send,
//     before the run starts — so we require the stop-button to be observed VISIBLE (run started) first,
//     then wait for it to hide (run finished).
//
// FALLBACK: if the mock `general-purpose` sub-agent does not run (nothing folds above the parent's
// 300), this does NOT fail silently — it records that clearly AND proves the reload/aggregate path by
// opening the pre-existing folded-subtree conversation `thread-1784906656170-iocwjw1`.
async (page) => {
  const BASE = 'http://localhost:5050';
  const PROVIDER = 'test-anthropic';
  const SHOT = 'B:/sources/LmDotnetTools/samples/LmStreaming.Sample/usage-live-subagent.png';
  const FALLBACK_THREAD = 'thread-1784906656170-iocwjw1';
  const parentBaseline = 300; // two parent generations x 150

  // Build the parent prompt programmatically so the nested-chain escaping is correct by construction
  // (inner <|instruction_start|>/<|instruction_end|> tags stay literal; inner quotes get escaped).
  const innerChain = JSON.stringify({
    instruction_chain: [
      { id: 'sub-tool', id_message: 'Sub-agent uses calculate', messages: [{ tool_call: [{ name: 'calculate', args: { a: 2, operation: 'add', b: 3 } }] }] },
      { id: 'sub-text', id_message: 'Sub-agent replies', messages: [{ text: 'hi from agent' }] },
    ],
  });
  const parent = {
    instruction_chain: [
      { id: 'parent', id_message: 'Delegate to sub-agent', messages: [{ tool_call: [{ name: 'Agent', args: { subagent_type: 'general-purpose', prompt: `<|instruction_start|>${innerChain}<|instruction_end|>` } }] }] },
      { id: 'parent2', id_message: 'Wrap up', messages: [{ text: 'Parent summary: delegation finished.' }] },
    ],
  };
  const PROMPT = `<|instruction_start|>${JSON.stringify(parent)}<|instruction_end|>`;

  const steps = [];
  const record = (name, pass, detail) => steps.push({ name, pass, detail });
  const tid = (id) => page.locator(`[data-testid="${id}"]`);
  const totalOf = (t) => parseInt((String(t).match(/Total:\s*(\d+)/) || [])[1] || '0', 10);
  const bannerText = async () => (await tid('usage-banner').textContent().catch(() => null)) ?? '';
  const send = async (text) => {
    await tid('chat-input-textarea').fill(text);
    await tid('send-button').click();
  };
  // Resilient provider pick: the provider dropdown re-renders as the list loads async, so the option
  // can detach mid-click. Open, let it settle, click, retry; confirm via the selector button label.
  const providerLabelOk = async () =>
    /Anthropic/i.test((await tid('provider-selector-button').textContent().catch(() => '')) ?? '');
  const selectProvider = async () => {
    for (let attempt = 0; attempt < 5; attempt++) {
      try {
        await tid('provider-selector-button').click();
        const opt = tid(`provider-option-${PROVIDER}`);
        await opt.waitFor({ state: 'visible', timeout: 8000 });
        await page.waitForTimeout(350); // let the async provider list settle
        await opt.click({ timeout: 5000 });
        if (await providerLabelOk()) return true;
      } catch {
        await page.keyboard.press('Escape').catch(() => {});
        await page.waitForTimeout(300);
      }
    }
    return providerLabelOk();
  };
  const threadIdsFromApi = async () =>
    page.evaluate(async () => {
      const r = await fetch('/api/conversations');
      const body = await r.json();
      const list = body.conversations ?? body ?? [];
      return list.map((c) => c.threadId);
    });
  const usageFor = async (id) =>
    page.evaluate(async (threadId) => {
      const r = await fetch(`/api/conversations/${threadId}/usage`);
      return r.ok ? await r.json() : { status: r.status };
    }, id);
  // Wait for the run to genuinely START (stop-button seen visible) then FINISH (idle), guarding the
  // post-click window where idle is trivially true before streaming begins.
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
    return started; // false => run never observed starting
  };

  let banner = '';
  let liveTotal = 0;
  let aggregateTotal = null;
  let newThreadId = null;
  let fallbackBannerText = null;

  try {
    await page.goto(BASE);
    await tid('chat-input-textarea').waitFor({ timeout: 20000 });
    // Let the app finish restoring the last-active conversation BEFORE clicking New Chat — otherwise the
    // async restore overwrites currentThreadId right after our click and the send lands in the restored
    // (old) conversation instead of a fresh thread. (Confirmed failure mode on this shared dev host.)
    await page.locator('[data-testid="conversation-item"]').first().waitFor({ timeout: 20000 }).catch(() => {});
    await page.waitForTimeout(1200);

    // 1. Fresh, EMPTY chat on the mock provider (General Assistant default => Agent + calculate wired).
    //    A fresh chat has no user messages and no usage-banner (it renders only once total > 0). Retry
    //    New Chat until the view is genuinely empty, so the send cannot append to a restored conversation.
    const isEmptyChat = async () =>
      (await page.locator('[data-testid="user-message-group"]').count()) === 0 &&
      (await tid('usage-banner').count()) === 0;
    let freshChat = false;
    for (let i = 0; i < 5 && !freshChat; i++) {
      await page.getByRole('button', { name: '+ New Chat' }).click().catch(() => {});
      await page.waitForTimeout(500);
      freshChat = await isEmptyChat();
    }
    const providerOk = await selectProvider();
    const stillEmpty = await isEmptyChat();
    const beforeIds = new Set(await threadIdsFromApi());
    record('fresh empty chat on test-anthropic', freshChat && providerOk && stillEmpty,
      { freshChat, providerOk, stillEmpty, beforeCount: beforeIds.size });

    // 2/3. Send the parent->Agent->sub-agent delegation; wait for the synchronous run to start & settle.
    await send(PROMPT);
    const ranToIdle = await waitRunStartThenIdle(45000);
    record('delegation run started and reached stream idle', ranToIdle, { ranToIdle });

    // 4. Resolve THIS run's thread id by diffing the conversation list (robust to the pinned old chat).
    for (let i = 0; i < 25 && !newThreadId; i++) {
      const after = await threadIdsFromApi();
      newThreadId = after.find((id) => !beforeIds.has(id)) ?? null;
      if (!newThreadId) await page.waitForTimeout(200);
    }
    record('a new conversation was created for this delegation', !!newThreadId, { newThreadId });

    // 5. Read the LIVE banner (NO reload). With the #196 live-fold fix it reaches 600 (> the 300 the two
    //    parent turns alone produce). Poll briefly to let the final ConversationUsageMessage frame land.
    for (let i = 0; i < 40; i++) {
      banner = await bannerText();
      if (totalOf(banner) > parentBaseline) break;
      await page.waitForTimeout(150);
    }
    liveTotal = totalOf(banner);

    const agg = newThreadId ? await usageFor(newThreadId) : { status: 'no-thread-id' };
    aggregateTotal = agg?.totalTokens ?? null;

    record(
      'LIVE banner Total > 300 (sub-agent usage folded live, no reload)',
      liveTotal > parentBaseline,
      { bannerText: banner, liveTotal, parentBaseline, newThreadId, aggregateTotal }
    );
    record(
      'server aggregate (/api/.../usage) > 300 (sub-agent folded into ledger)',
      typeof aggregateTotal === 'number' && aggregateTotal > parentBaseline,
      { aggregateTotal, perModel: agg?.perModel }
    );
    record(
      'LIVE banner equals the server aggregate (cross-check)',
      typeof aggregateTotal === 'number' && liveTotal === aggregateTotal,
      { liveTotal, aggregateTotal }
    );

    await page.screenshot({ path: SHOT }).catch(() => {});

    // FALLBACK — only if nothing folded above the parent baseline (the sub-agent did not run). Report it
    // explicitly and still prove the aggregate/reload path via the known folded-subtree conversation.
    const subAgentFolded = liveTotal > parentBaseline || (typeof aggregateTotal === 'number' && aggregateTotal > parentBaseline);
    if (!subAgentFolded) {
      record('sub-agent did NOT fold above parent baseline (Agent template unavailable in live app?)', false, {
        liveTotal, aggregateTotal,
        note: 'Falling back to the pre-existing folded-subtree conversation to prove the aggregate/reload path.',
      });
      await page.locator(`[data-testid="conversation-item"][data-thread-id="${FALLBACK_THREAD}"]`).click({ timeout: 8000 }).catch(() => {});
      for (let i = 0; i < 40; i++) {
        fallbackBannerText = await bannerText();
        if (totalOf(fallbackBannerText) > 1000000) break;
        await page.waitForTimeout(150);
      }
      record('FALLBACK: known conversation banner Total is in the millions (folded subtree)',
        totalOf(fallbackBannerText) > 1000000, { fallbackBannerText });
    }
  } catch (e) {
    record('exception', false, String((e && e.stack) || e));
    await page.screenshot({ path: SHOT }).catch(() => {});
  }

  const failures = steps.filter((s) => !s.pass).map((s) => s.name);
  return { pass: failures.length === 0, failures, steps, bannerText: banner, liveTotal, parentBaseline, aggregateTotal, newThreadId, fallbackBannerText };
}
