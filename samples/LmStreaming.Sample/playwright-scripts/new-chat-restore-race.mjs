// new-chat-restore-race.mjs — single-call Playwright flow for the page-load restore racing the user.
// On load the client restores the most recently used conversation, but only after the conversation
// list, modes, tools, providers and workspaces have loaded. A choice the user makes inside that window
// must win. Run it in ONE call:
//
//   browser_run_code_unsafe({ filename: "samples/LmStreaming.Sample/playwright-scripts/new-chat-restore-race.mjs" })
//
// The window is made deterministic by holding one load request open with page.route
// (the tool catalog, the slowest of the five loads; it can take seconds on its own when the sandbox
// gateway is slow). Each case waits for that load to finish before judging:
//   A. the user clicks New chat, then sends. The view must stay on the new chat, the send must start a
//      NEW conversation, and the previously used one must not receive the message.
//   B. the list is already on screen; the user opens an older conversation. It must stay open when
//      the restore would have picked the newest.
//   C. control: nothing held, no click - the restore still opens the most recently used conversation.
// Returns { pass, failures, steps, badResponses }. Uses the `test` mock provider. Adjust BASE.
async (page) => {
  const BASE = 'http://localhost:5077';
  const HOLD_MS = 1500;
  const stamp = Date.now().toString(36);
  const steps = [];
  let stage = 'start';
  const badResponses = [];
  page.on('response', (r) => {
    if (r.status() >= 400) badResponses.push(`${r.status()} ${r.request().method()} ${r.url()}`);
  });

  const record = (name, pass, detail) => steps.push({ name, pass, detail });
  const tid = (id) => page.locator(`[data-testid="${id}"]`);
  const sidebarNewChat = () => tid('sidebar-new-chat');
  const activeId = () =>
    page.evaluate(() => document.querySelector('.conversation-item.active')?.getAttribute('data-thread-id') ?? null);
  const waitIdle = async () => {
    await tid('stop-button').waitFor({ state: 'hidden', timeout: 60000 });
    await tid('send-button').waitFor({ state: 'visible', timeout: 60000 });
  };
  const send = async (text) => {
    await tid('chat-input-textarea').fill(text);
    await tid('send-button').click();
    await tid('stop-button').waitFor({ state: 'visible', timeout: 10000 }).catch(() => {});
    await waitIdle();
  };
  const useTestProvider = async () => {
    await tid('provider-selector-button').click();
    await tid('provider-option-test').click();
  };
  const messagesOf = async (threadId) => {
    const r = await page.request.get(`${BASE}/api/conversations/${threadId}/messages`);
    return r.ok() ? JSON.stringify(await r.json()) : `HTTP ${r.status()}`;
  };
  const newConversation = async (text) => {
    stage = `new conversation: ${text}`;
    await sidebarNewChat().click();
    await useTestProvider();
    await send(text);
    await page.waitForFunction(() => !!document.querySelector('.conversation-item.active'), null, { timeout: 15000 });
    return activeId();
  };
  // Holds matching GETs for HOLD_MS, then lets them through unchanged.
  const hold = async (pattern) => {
    const handler = async (route) => {
      if (route.request().method() === 'GET') await page.waitForTimeout(HOLD_MS);
      // A reload while held cancels the request; Playwright then reports the route as handled.
      await route.continue().catch(() => {});
    };
    await page.route(pattern, handler);
    return () => page.unroute(pattern, handler);
  };
  const LIST = /\/api\/conversations(\?|$)/;
  const TOOLS = /\/api\/tools(\?|$)/;
  // The restore runs once every load has answered; the tool catalog is the one held.
  const restoreWindowClosed = async () => {
    await page.waitForResponse((r) => TOOLS.test(r.url()) && r.request().method() === 'GET', { timeout: 60000 });
    await page.waitForTimeout(1500);
  };

  try {
    await page.goto(BASE);
    await sidebarNewChat().waitFor({ state: 'visible', timeout: 20000 });

    // Two conversations: `older` first, then `newest`, which the restore picks.
    const older = await newConversation(`b2 ${stamp} older`);
    const newest = await newConversation(`b2 ${stamp} newest`);
    record('seeded two conversations', !!older && !!newest && older !== newest, { older, newest });

    stage = 'A';
    // --- A: New chat while the conversation list is still loading.
    let release = await hold(TOOLS);
    await page.reload();
    await page.waitForResponse((r) => LIST.test(r.url()) && r.request().method() === 'GET', { timeout: 20000 });
    await sidebarNewChat().click();
    await restoreWindowClosed();
    await release();
    const afterRestoreA = await activeId();
    const shownA = await page.locator('[data-testid="user-message-group"]').count();
    record('A: New chat survives the late restore (nothing selected, empty chat)', afterRestoreA === null && shownA === 0, {
      afterRestoreA,
      shownA,
      newest,
    });

    stage = 'A send';
    const probe = `b2 ${stamp} probe after new chat`;
    await useTestProvider();
    await send(probe);
    await page.waitForFunction(() => !!document.querySelector('.conversation-item.active'), null, { timeout: 15000 });
    const probeThread = await activeId();
    const newestBody = await messagesOf(newest);
    record('A: the send started a new conversation', !!probeThread && probeThread !== newest && probeThread !== older, {
      probeThread,
      newest,
    });
    record('A: the previously used conversation did not receive it', !newestBody.includes(probe), {
      newestHasProbe: newestBody.includes(probe),
    });

    // The probe conversation is now the newest; B opens `older` while providers are held.
    const newestNow = probeThread;

    stage = 'B';
    // --- B: open an older conversation while the provider catalog is still loading.
    release = await hold(TOOLS);
    await page.reload();
    const olderRow = page.locator(`.conversation-item[data-thread-id="${older}"]`);
    await olderRow.waitFor({ state: 'visible', timeout: 20000 });
    await olderRow.click();
    await restoreWindowClosed();
    await release();
    const afterRestoreB = await activeId();
    record('B: the conversation the user opened stays open', afterRestoreB === older, {
      afterRestoreB,
      older,
      newestNow,
    });

    stage = 'C';
    // --- C: control - no user choice, the restore opens the most recently used conversation.
    await page.reload();
    await page.waitForFunction(() => !!document.querySelector('.conversation-item.active'), null, { timeout: 60000 });
    const afterRestoreC = await activeId();
    // Opening `older` in B sent nothing, so the probe conversation is still the most recently used.
    record('C: with no user choice the restore still opens the most recent', afterRestoreC === newestNow, {
      afterRestoreC,
      newestNow,
    });
  } catch (e) {
    const dom = await page
      .evaluate(() => ({
        rows: [...document.querySelectorAll('.conversation-item')].slice(0, 5).map((n) => ({
          cls: n.className,
          id: n.getAttribute('data-thread-id'),
          testid: n.getAttribute('data-testid'),
        })),
        userGroups: document.querySelectorAll('[data-testid="user-message-group"]').length,
        error: document.querySelector('[data-testid="error-banner"]')?.textContent?.trim() ?? null,
      }))
      .catch(() => null);
    record('script error', false, { stage, error: String(e).slice(0, 400), dom });
  }

  const failures = steps.filter((s) => !s.pass).map((s) => s.name);
  return { pass: failures.length === 0, failures, steps, badResponses };
}
