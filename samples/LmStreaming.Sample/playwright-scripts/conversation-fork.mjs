// conversation-fork.mjs — single-call Playwright flow for conversation forks (Fork from here, Edit in
// fork, branch switcher, fork banner, sidebar nesting, deleted original, no buttons while streaming,
// no buttons on CLI providers). Run it in ONE call:
//
//   browser_run_code_unsafe({ filename: "samples/LmStreaming.Sample/playwright-scripts/conversation-fork.mjs" })
//
// Returns { pass, failures, steps, consoleErrors, badResponses }. Uses the `test` mock provider (plain
// text prompts) and `claude-mock` (CLI-backed) for the no-fork-buttons case. Adjust BASE.
async (page) => {
  const BASE = 'http://localhost:5077';
  // Relative, under the gitignored `.logs/`: Playwright resolves a relative screenshot path against the
  // MCP server's cwd, the repo/worktree root (see ask-question-notify-client.mjs).
  const SHOTS = '.logs/fork-manual';
  const LONG =
    '<|instruction_start|>{"instruction_chain":[{"id":"long-text","id_message":"Long response","messages":[{"text_message":{"length":300}}]}]}<|instruction_end|>';
  const stamp = Date.now().toString(36);
  const T1 = `fork-e2e ${stamp} turn one`;
  const T2 = `fork-e2e ${stamp} turn two`;
  const T3 = `fork-e2e ${stamp} fork turn three`;

  const steps = [];
  const consoleErrors = [];
  const badResponses = [];
  const onConsole = (m) => { if (m.type() === 'error') consoleErrors.push(m.text().slice(0, 300)); };
  const onResponse = (r) => { if (r.status() >= 400) badResponses.push(`${r.status()} ${r.request().method()} ${r.url()}`); };
  page.on('console', onConsole);
  page.on('response', onResponse);
  const messageGets = [];
  const onRequest = (r) => { if (r.method() === 'GET' && /\/messages(\?|$)/.test(r.url())) messageGets.push(`${new Date().toISOString()} ${r.url()}`); };
  page.on('request', onRequest);

  const record = (name, pass, detail) => steps.push({ name, pass, detail });
  const tid = (id) => page.locator(`[data-testid="${id}"]`);
  const SHOT_PREFIX = 'ui-r3-';
  const shot = (name) => page.screenshot({ path: `${SHOTS}/${SHOT_PREFIX}${name}.png`, fullPage: false }).catch(() => {});
  const waitStreaming = () => tid('stop-button').waitFor({ state: 'visible', timeout: 10000 });
  const waitIdle = async () => {
    await tid('stop-button').waitFor({ state: 'hidden', timeout: 60000 });
    await tid('send-button').waitFor({ state: 'visible', timeout: 60000 });
  };
  const send = async (text) => {
    await tid('chat-input-textarea').fill(text);
    await tid('send-button').click();
    await waitStreaming().catch(() => {});
    await waitIdle();
  };
  const activeId = () =>
    page.evaluate(() => document.querySelector('.conversation-item.active')?.getAttribute('data-thread-id') ?? null);
  const waitActiveChange = async (prev) => {
    await page.waitForFunction(
      (p) => {
        const id = document.querySelector('.conversation-item.active')?.getAttribute('data-thread-id');
        return !!id && id !== p;
      },
      prev,
      { timeout: 15000 }
    );
    return activeId();
  };
  const waitActive = (id) =>
    page.waitForFunction(
      (t) => document.querySelector('.conversation-item.active')?.getAttribute('data-thread-id') === t,
      id,
      { timeout: 15000 }
    );
  const waitCount = (testid, n) =>
    page
      .waitForFunction(
        ([t, k]) => document.querySelectorAll(`[data-testid="${t}"]`).length === k,
        [testid, n],
        { timeout: 15000 }
      )
      .then(() => true)
      .catch(() => false);
  const dom = () =>
    page.evaluate(() => {
      const q = (s) => [...document.querySelectorAll(s)];
      return {
        active: document.querySelector('.conversation-item.active')?.getAttribute('data-thread-id') ?? null,
        users: q('[data-testid="user-message-group"]').map((n) => n.textContent.trim().slice(0, 60)),
        assistants: q('[data-testid="assistant-message-group"]').length,
        banner: document.querySelector('[data-testid="fork-banner"]')?.textContent.replace(/\s+/g, ' ').trim() ?? null,
        bannerOpen: !!document.querySelector('[data-testid="fork-banner-open"]'),
        switchers: q('[data-testid="branch-switcher-label"]').map((n) => n.textContent.trim()),
        forkButtons: q('[data-testid="fork-from-here-button"]').length,
        editButtons: q('[data-testid="edit-in-fork-button"]').length,
        forkError: document.querySelector('[data-testid="fork-error"]')?.textContent.trim() ?? null,
        forkRows: q('[data-testid="sidebar-fork-row"]').map((n) => ({
          id: n.getAttribute('data-thread-id'),
          root: n.getAttribute('data-root-thread-id'),
          text: n.querySelector('.conversation-content')?.textContent.replace(/\s+/g, ' ').trim(),
        })),
        deletedOriginals: q('[data-testid="sidebar-deleted-original"]').map((n) => ({
          id: n.getAttribute('data-thread-id'),
          text: n.textContent.replace(/\s+/g, ' ').trim(),
        })),
        composer: document.querySelector('[data-testid="chat-input-textarea"]')?.value ?? null,
      };
    });
  const api = (path) => page.evaluate(async (p) => (await fetch(p)).json(), path);
  const openSidebar = async (id) => {
    await page.locator(`[data-thread-id="${id}"] .conversation-select-btn`).first().click();
    await waitActive(id);
  };

  let origId = null;
  let forkId = null;
  let editForkId = null;
  let fork2Id = null;
  let forkMidId = null;

  try {
    // ---- Scenario 1: original with 2 turns, Fork from here on turn 2's reply ----
    await page.goto(BASE);
    await tid('chat-input-textarea').waitFor({ timeout: 20000 });
    // Let the page-load restore of the last conversation finish first: a New chat clicked before it
    // lands is overridden by it (the send then goes into the restored conversation).
    await page.waitForLoadState('networkidle').catch(() => {});
    await tid('sidebar-new-chat').click();
    await tid('provider-selector-button').click();
    await tid('provider-option-test').click();
    const priorUrl = page.url();
    const newChatDom = await dom();
    await send(T1);
    const afterT1 = await dom();
    const existing = await api('/api/conversations?limit=50');
    const t1Row = (existing.conversations || existing).find((r) => r.threadId === afterT1.active);
    record('s0.new-chat-first-send-starts-a-new-conversation', afterT1.users.length === 1 && !t1Row?.forkedFrom, {
      priorUrl, newChatDom: { active: newChatDom.active, users: newChatDom.users.length }, afterT1: { active: afterT1.active, users: afterT1.users }, t1Row,
    });
    await send(T2);
    origId = await activeId();
    const orig = await dom();
    await shot('01-original');
    record('s1.original-has-two-turns-and-buttons', !!origId && orig.users.length === 2 && orig.forkButtons >= 2,{ origId, ...orig });

    // A live (not reloaded) conversation should learn its user rows' stored ids after each run, so
    // "Edit in fork" appears without a reload.
    const liveEdit = await waitCount('edit-in-fork-button', 2);
    record('s1.live-user-messages-get-edit-in-fork', liveEdit, {
      editButtons: (await dom()).editButtons,
      messageGets: messageGets.filter((u) => u.includes(origId)),
    });

    // Fork from turn 2's reply (the tail of the original).
    await tid('assistant-message-group').last().hover();
    await tid('fork-from-here-button').last().click();
    forkId = await waitActiveChange(origId);
    await tid('fork-banner').waitFor({ timeout: 15000 });
    await waitCount('user-message-group', 2);
    await tid('branch-switcher').first().waitFor({ timeout: 5000 }).catch(() => {});
    const f1 = await dom();
    await shot('02-fork-opened');
    const row = f1.forkRows.find((r) => r.id === forkId);
    record('s1.fork-opened-with-banner', !!forkId && forkId !== origId && !!f1.banner, { forkId, banner: f1.banner });
    record('s1.sidebar-nests-under-original', !!row && row.root === origId && /from msg \d+/.test(row.text ?? ''), { row });
    record('s1.history-is-turns-1-2', f1.users.length === 2 && f1.assistants === 2 && f1.users[1].includes('turn two'), { users: f1.users, assistants: f1.assistants });
    record('s1.switcher-at-tail-fork-2-of-2', f1.switchers.length === 1 && f1.switchers[0] === '2 / 2', { switchers: f1.switchers, branches: await api(`/api/conversations/${forkId}/branches`) });
    if (f1.switchers.length === 1) {
      await tid('branch-switcher-prev').first().click();
      await waitActive(origId);
      await waitCount('user-message-group', 2);
      await tid('branch-switcher').first().waitFor({ timeout: 10000 }).catch(() => {});
      const tOrig = await dom();
      await shot('02c-tail-switch-to-original');
      await tid('branch-switcher-next').first().click();
      await waitActive(forkId);
      await tid('fork-banner').waitFor({ timeout: 10000 });
      await tid('branch-switcher').first().waitFor({ timeout: 10000 }).catch(() => {});
      const tFork = await dom();
      record('s1.tail-arrows-switch-conversations', tOrig.active === origId && tOrig.banner === null && tOrig.switchers[0] === '1 / 2' && tFork.active === forkId && tFork.switchers[0] === '2 / 2', {
        onOrig: { active: tOrig.active, switchers: tOrig.switchers, users: tOrig.users.length }, backOnFork: { active: tFork.active, switchers: tFork.switchers },
      });
    }

    // Fork from turn 1's reply (inside shared history): the original continues past it, so the
    // switcher must show and its arrows switch conversations.
    await openSidebar(origId);
    await waitCount('user-message-group', 2);
    await tid('assistant-message-group').first().hover();
    await tid('fork-from-here-button').first().click();
    forkMidId = await waitActiveChange(origId);
    await tid('fork-banner').waitFor({ timeout: 15000 });
    await waitCount('user-message-group', 1);
    await tid('branch-switcher').first().waitFor({ timeout: 10000 }).catch(() => {});
    const m1 = await dom();
    await shot('02b-mid-fork-switcher');
    record('s1b.mid-fork-shows-switcher', m1.users.length === 1 && m1.switchers.length === 1 && /^\d \/ 2$/.test(m1.switchers[0]), { forkMidId, users: m1.users, switchers: m1.switchers, branches: await api(`/api/conversations/${forkMidId}/branches`) });
    if (m1.switchers.length === 1) {
      const label0 = m1.switchers[0];
      const toOrigBtn = label0 === '2 / 2' ? 'branch-switcher-prev' : 'branch-switcher-next';
      const backBtn = label0 === '2 / 2' ? 'branch-switcher-next' : 'branch-switcher-prev';
      await tid(toOrigBtn).first().click();
      await waitActive(origId);
      await waitCount('user-message-group', 2);
      await tid('branch-switcher').first().waitFor({ timeout: 10000 }).catch(() => {});
      const onOrig = await dom();
      await shot('03-switched-to-original');
      await tid(backBtn).first().click();
      await waitActive(forkMidId);
      await tid('fork-banner').waitFor({ timeout: 10000 });
      await tid('branch-switcher').first().waitFor({ timeout: 10000 }).catch(() => {});
      const backOnFork = await dom();
      record('s1b.arrows-switch-conversations', onOrig.active === origId && onOrig.banner === null && onOrig.switchers.length >= 1 && onOrig.switchers.every((l) => l.startsWith('1 /')) && backOnFork.active === forkMidId && backOnFork.switchers[0] === label0, {
        onOrig: { active: onOrig.active, switchers: onOrig.switchers, banner: onOrig.banner, users: onOrig.users.length },
        backOnFork: { active: backOnFork.active, switchers: backOnFork.switchers },
      });
    }
    await openSidebar(forkId);
    await waitCount('user-message-group', 2);

    // ---- Scenario 2: send in the fork, reload ----
    await send(T3);
    await waitCount('user-message-group', 3);
    const f2 = await dom();
    record('s2.fork-send-appends-after-shared-history', f2.users.length === 3 && f2.users[2].includes('fork turn three') && f2.assistants === 3, { users: f2.users, assistants: f2.assistants });
    record('s2.live-edit-in-fork-on-fork-turn', await waitCount('edit-in-fork-button', 3), { editButtons: (await dom()).editButtons });
    const origMsgs = await api(`/api/conversations/${origId}/messages`).catch((e) => String(e));
    await page.reload();
    await tid('chat-input-textarea').waitFor({ timeout: 20000 });
    let reloadRestored = true;
    try {
      await waitActive(forkId);
    } catch {
      reloadRestored = false;
      await page.goto(`${BASE}/?threadId=${forkId}`);
      await waitActive(forkId);
    }
    await tid('fork-banner').waitFor({ timeout: 15000 }).catch(() => {});
    await waitCount('user-message-group', 3);
    await tid('branch-switcher').first().waitFor({ timeout: 10000 }).catch(() => {});
    const f3 = await dom();
    await shot('04-fork-after-reload');
    record('s2.reload-restores-open-conversation', reloadRestored, { reloadRestored });
    record('s2.reload-keeps-history-banner-switcher', f3.users.length === 3 && !!f3.banner && f3.switchers.length >= 1, {
      users: f3.users, banner: f3.banner, switchers: f3.switchers,
      origMessageCount: Array.isArray(origMsgs) ? origMsgs.length : (origMsgs?.messages?.length ?? origMsgs),
    });

    // ---- Scenario 3: Edit in fork on turn 2's user message (from the original) ----
    await openSidebar(origId);
    await waitCount('user-message-group', 2);
    await tid('user-message-group').nth(1).hover();
    await tid('edit-in-fork-button').nth(1).click();
    editForkId = await waitActiveChange(origId);
    await tid('fork-banner').waitFor({ timeout: 15000 });
    await waitCount('user-message-group', 1);
    await page
      .waitForFunction((t) => document.querySelector('[data-testid="chat-input-textarea"]')?.value === t, T2, { timeout: 5000 })
      .catch(() => {});
    const e1 = await dom();
    await shot('05-edit-in-fork');
    const eRow = e1.forkRows.find((r) => r.id === editForkId);
    record('s3.edit-in-fork-prefills-composer', e1.composer === T2, { composer: e1.composer });
    record('s3.edit-in-fork-history-ends-before-message', e1.users.length === 1 && e1.users[0].includes('turn one'), { users: e1.users, assistants: e1.assistants });
    record('s3.edit-fork-nested-under-original', !!eRow && eRow.root === origId, { eRow });
    await tid('chat-input-textarea').fill('');

    // ---- Scenario 4: fork the fork ----
    await openSidebar(forkId);
    await waitCount('user-message-group', 3);
    await tid('assistant-message-group').first().hover();
    await tid('fork-from-here-button').first().click();
    fork2Id = await waitActiveChange(forkId);
    await tid('fork-banner').waitFor({ timeout: 15000 });
    await waitCount('user-message-group', 1);
    const g1 = await dom();
    await shot('06-fork-of-fork');
    const g2Row = g1.forkRows.find((r) => r.id === fork2Id);
    record('s4.fork-of-fork-one-level-under-original', !!g2Row && g2Row.root === origId && [forkId, forkMidId, editForkId, fork2Id].every((id) => g1.forkRows.find((r) => r.id === id)?.root === origId), { g2Row, forkRows: g1.forkRows });
    // Forked inside shared history (seq 2 is the original's row), so the switcher at that point must
    // offer distinct conversations, the original among them, not the fork itself twice.
    const g2Branches = await api(`/api/conversations/${fork2Id}/branches`);
    const g2Opts = (g2Branches.points ?? []).flatMap((p) => p.options.map((o) => o.threadId));
    const oneCurrent = (g2Branches.points ?? []).every((p) => p.options.filter((o) => o.current).length === 1 && p.options.find((o) => o.current)?.threadId === fork2Id);
    record('s4.fork-of-fork-switcher-options-distinct-and-include-original', g2Opts.length > 0 && new Set(g2Opts).size === g2Opts.length && g2Opts.includes(origId) && oneCurrent, {
      oneCurrent,
      switchers: g1.switchers, branches: g2Branches,
    });
    record('s4.fork-of-fork-banner-and-label', /Forked from/.test(g1.banner ?? '') && /from ".*" · msg \d+/.test(g2Row?.text ?? ''), { banner: g1.banner, users: g1.users });

    // ---- Scenario 6: no fork buttons while streaming ----
    await tid('chat-input-textarea').fill(LONG);
    await tid('send-button').click();
    let streamingSeen = true;
    await waitStreaming().catch(() => { streamingSeen = false; });
    const mid = await dom();
    await shot('07-streaming-no-fork-buttons');
    await waitIdle();
    const after = await dom();
    record('s6.no-fork-buttons-while-streaming', streamingSeen && mid.forkButtons === 0 && mid.editButtons === 0 && after.forkButtons > 0, {
      streamingSeen, mid: { fork: mid.forkButtons, edit: mid.editButtons }, after: { fork: after.forkButtons, edit: after.editButtons },
    });

    // ---- Scenario 5: delete the original ----
    // Auto-accept the delete confirm() in-page: a real dialog becomes MCP "modal state" and the
    // run's result is lost.
    await page.evaluate(() => { window.confirm = () => true; });
    await page.locator(`[data-testid="conversation-item"][data-thread-id="${origId}"] .delete-btn`).click();
    await page.locator(`[data-testid="sidebar-deleted-original"][data-thread-id="${origId}"]`).waitFor({ timeout: 15000 }).catch(() => {});
    const d1 = await dom();
    const list = await api('/api/conversations');
    const rows = list.conversations || list;
    await shot('08-original-deleted');
    const del = d1.deletedOriginals.find((r) => r.id === origId);
    record('s5.original-becomes-deleted-header', !!del && /\(deleted original\)/.test(del.text), { del, apiOrig: rows.find((r) => r.threadId === origId) });
    record('s5.forks-still-listed', [forkId, forkMidId, editForkId, fork2Id].every((id) => d1.forkRows.some((r) => r.id === id)), { forkRows: d1.forkRows });
    await openSidebar(forkId);
    await tid('fork-banner').waitFor({ timeout: 15000 }).catch(() => {});
    await waitCount('user-message-group', 3);
    await tid('branch-switcher').first().waitFor({ timeout: 5000 }).catch(() => {});
    const d2 = await dom();
    await shot('09-fork-after-original-deleted');
    record('s5.fork-still-loads-after-delete', d2.active === forkId && d2.users.length === 3, { users: d2.users });
    record('s5.banner-open-link-gone', !!d2.banner && !d2.bannerOpen, { banner: d2.banner, bannerOpen: d2.bannerOpen });
    const d2Branches = await api(`/api/conversations/${forkId}/branches`);
    const d2Opts = (d2Branches.points ?? []).flatMap((p) => p.options.map((o) => o.threadId));
    record('s5.switcher-after-delete-excludes-original', !d2Opts.includes(origId) && new Set(d2Opts).size === d2Opts.length, { switchers: d2.switchers, branches: d2Branches });
    const origStatus = await page.evaluate(async (t) => ({
      messages: (await fetch(`/api/conversations/${t}/messages`)).status,
      branches: (await fetch(`/api/conversations/${t}/branches`)).status,
    }), origId);
    record('s5.deleted-original-rest-404', origStatus.messages === 404, origStatus);

    // Same checks after a reload (the list comes from the server, deleted:true included).
    await page.reload();
    await tid('chat-input-textarea').waitFor({ timeout: 20000 });
    await tid('sidebar-deleted-original').first().waitFor({ timeout: 10000 }).catch(() => {});
    await openSidebar(forkId).catch(() => {});
    await tid('fork-banner').waitFor({ timeout: 15000 }).catch(() => {});
    const d3 = await dom();
    await shot('09b-after-delete-reload');
    const del3 = d3.deletedOriginals.find((r) => r.id === origId);
    record('s5.after-reload-deleted-header-and-no-open-link', !!del3 && !!d3.banner && !d3.bannerOpen, {
      del3, banner: d3.banner, bannerOpen: d3.bannerOpen,
      familyRows: d3.forkRows.filter((r) => [forkId, forkMidId, editForkId, fork2Id].includes(r.id)),
    });

    // ---- Scenario 7: CLI provider has no fork buttons ----
    await tid('sidebar-new-chat').click();
    await tid('provider-selector-button').click();
    await tid('provider-option-claude-mock').click();
    await send(`fork-e2e ${stamp} cli`);
    const c1 = await dom();
    await shot('10-cli-no-fork-buttons');
    record('s7.cli-provider-no-fork-buttons', c1.users.length >= 1 && c1.forkButtons === 0 && c1.editButtons === 0, { active: c1.active, users: c1.users, fork: c1.forkButtons, edit: c1.editButtons });
  } catch (e) {
    record('exception', false, String((e && e.stack) || e).slice(0, 1500));
    await shot('99-exception');
  } finally {
    page.off('console', onConsole);
    page.off('response', onResponse);
    page.off('request', onRequest);
  }

  const failures = steps.filter((s) => !s.pass).map((s) => s.name);
  return { pass: failures.length === 0, failures, ids: { origId, forkId, forkMidId, editForkId, fork2Id }, steps, consoleErrors, badResponses }
}
