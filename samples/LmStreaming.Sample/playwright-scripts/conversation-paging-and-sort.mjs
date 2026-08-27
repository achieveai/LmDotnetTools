// conversation-paging-and-sort.mjs — single-call Playwright validation for the conversation
// sidebar paging + sort-mode work. Run the WHOLE flow in ONE call:
//
//   browser_run_code_unsafe({ filename: "samples/LmStreaming.Sample/playwright-scripts/conversation-paging-and-sort.mjs" })
//
// Returns { pass, failures, steps } — assert only DETERMINISTIC, browser-observable state.
//
// WHAT IT GUARDS
// The sidebar used to take a 50-row page from the store and only THEN drop agent-owned
// (`subagent-*` / `workflow-*`) rows. Because LastUpdated is bumped on every completed run and
// background sub-agent runs are constant, agent-owned rows crowded the front of the ordering and
// the page arrived at the browser almost empty of real conversations — silently hiding everything
// older. A check that seeds only a handful of threads CANNOT see that bug, so this script requires
// the seeded corpus below, where agent-owned rows outnumber a full page on their own.
//
// PREREQ — seed the store the app runs against (scratchpad/seed-conversations.sh):
//   45 real `thread-*`      (more than one 30-row page, so paging is exercised)
//   60 `subagent-*` + 6 `workflow-*`, ALL with lastUpdated NEWER than every real conversation
//   Real thread N is created at T0+N but last-used at T0+(45-N), so creation order and last-used
//   order DISAGREE — without that the two sort modes would be indistinguishable and every sort
//   assertion below would pass vacuously.
//   => under `lastUsed` the head row is "Real conversation 01"
//   => under `created`  the head row is "Real conversation 45"
//
// The app must serve the CURRENT client code. In Development it does NOT serve wwwroot/dist — it
// proxies /dist to a Vite dev server, so a stale or foreign Vite instance silently serves another
// tree's bundle. BASE must be the BACKEND origin so /api and /ws stay same-origin.
async (page) => {
  const BASE = 'http://localhost:5077';
  const PAGE_SIZE = 30;
  const REAL_TOTAL = 45;

  const failures = [];
  const steps = [];
  const check = (name, ok, detail) => {
    steps.push({ name, ok: !!ok, detail });
    if (!ok) {
      failures.push(`${name}: ${detail}`);
    }
    return !!ok;
  };

  const api = async (qs) =>
    page.evaluate(async (u) => {
      const r = await fetch(u);
      return { status: r.status, body: r.ok ? await r.json() : null };
    }, `${BASE}/api/conversations${qs}`);

  const agentOwned = (ids) =>
    ids.filter((id) => id.startsWith('subagent-') || id.startsWith('workflow-'));

  const sidebarRows = () =>
    page.$$eval('[data-testid="conversation-item"]', (els) =>
      els.map((e) => ({
        threadId: e.getAttribute('data-thread-id') || '',
        title: (e.textContent || '').trim(),
      }))
    );

  const waitForHead = (text) =>
    page
      .waitForFunction(
        (t) => {
          const first = document.querySelector('[data-testid="conversation-item"]');
          return !!first && (first.textContent || '').includes(t);
        },
        text,
        { timeout: 15000 }
      )
      .catch(() => {});

  const chooseSort = async (mode) => {
    await page.click('[data-testid="sort-mode-button"]');
    await page.click(`[data-testid="sort-mode-option-${mode}"]`);
  };

  // The sort mode PERSISTS in localStorage, so a previous run of this script leaves the app in
  // whatever mode it finished in. Start from a known state or the run is not idempotent: the
  // "switching sort resets paging" assertion below silently becomes "re-picking the mode you are
  // already on resets paging", which is a different (and wrong) claim — it is a no-op by design.
  // This is the flake that a single green run hides and a second run exposes.
  await page.goto(BASE, { waitUntil: 'domcontentloaded' });
  await page.evaluate((key) => {
    try {
      localStorage.removeItem(key);
    } catch {
      /* storage unavailable - the app falls back to the default mode anyway */
    }
  }, 'lmstreaming.conversations.sortMode');
  await page.reload({ waitUntil: 'domcontentloaded' });
  await page.waitForSelector('[data-testid="conversation-item"]', { timeout: 30000 });

  const startHead = (await sidebarRows())[0]?.title || '';
  check(
    'ui/the run starts in the default lastUsed mode',
    startHead.includes('Real conversation 01'),
    `head is "${startHead}" - expected the lastUsed default after clearing the stored mode`
  );

  // ---------- API level: the actual defect ----------
  const p1 = await api(`?limit=${PAGE_SIZE}&offset=0`);
  const p1Ids = (p1.body || []).map((c) => c.threadId);
  check(
    'api/page1 returns a FULL page of real conversations',
    p1Ids.length === PAGE_SIZE,
    `expected ${PAGE_SIZE}, got ${p1Ids.length}. A short page here is the filter-after-Take bug.`
  );
  check(
    'api/page1 contains no agent-owned rows',
    agentOwned(p1Ids).length === 0,
    `leaked: ${agentOwned(p1Ids).slice(0, 5).join(', ')}`
  );

  const p2 = await api(`?limit=${PAGE_SIZE}&offset=${PAGE_SIZE}`);
  const p2Ids = (p2.body || []).map((c) => c.threadId);
  check(
    'api/page2 returns the remainder',
    p2Ids.length === REAL_TOTAL - PAGE_SIZE,
    `expected ${REAL_TOTAL - PAGE_SIZE}, got ${p2Ids.length}`
  );
  check(
    'api/page2 does not overlap page1',
    p2Ids.every((id) => !p1Ids.includes(id)),
    `overlap: ${p2Ids.filter((id) => p1Ids.includes(id)).join(', ')}`
  );
  check(
    'api/every real conversation is reachable across pages',
    new Set([...p1Ids, ...p2Ids]).size === REAL_TOTAL,
    `distinct reachable = ${new Set([...p1Ids, ...p2Ids]).size}, expected ${REAL_TOTAL}`
  );

  // ---------- API level: the sort modes are genuinely different ----------
  const lastUsed = await api('?limit=5&offset=0&sort=lastUsed');
  const created = await api('?limit=5&offset=0&sort=created');
  const luFirst = lastUsed.body?.[0]?.title ?? '(none)';
  const crFirst = created.body?.[0]?.title ?? '(none)';
  check(
    'api/sort=lastUsed heads with the most recently used',
    luFirst === 'Real conversation 01',
    `got "${luFirst}"`
  );
  check(
    'api/sort=created heads with the most recently created',
    crFirst === 'Real conversation 45',
    `got "${crFirst}"`
  );
  check(
    'api/the two sort modes actually differ',
    luFirst !== crFirst,
    'both modes returned the same head row - the seed corpus cannot distinguish them'
  );

  const bogus = await api('?limit=5&sort=notasort');
  check(
    'api/an unrecognised sort is rejected, not silently ignored',
    bogus.status === 400,
    `expected 400, got ${bogus.status}`
  );

  // ---------- UI: incremental loading ----------
  const initial = await sidebarRows();
  check(
    'ui/first paint renders one page',
    initial.length === PAGE_SIZE,
    `expected ${PAGE_SIZE} rows, got ${initial.length}`
  );
  check(
    'ui/no agent-owned row in the sidebar',
    agentOwned(initial.map((r) => r.threadId)).length === 0,
    `leaked: ${agentOwned(initial.map((r) => r.threadId)).slice(0, 5).join(', ')}`
  );

  // `.sidebar-content` is the real scroller (flex:1; overflow-y:auto), NOT the outer <aside>.
  await page.$eval('.sidebar-content', (el) => {
    el.scrollTop = el.scrollHeight;
  });
  await page
    .waitForFunction(
      (n) => document.querySelectorAll('[data-testid="conversation-item"]').length >= n,
      REAL_TOTAL,
      { timeout: 15000 }
    )
    .catch(() => {});

  const afterScroll = await sidebarRows();
  check(
    'ui/scrolling loads the older conversations',
    afterScroll.length === REAL_TOTAL,
    `expected ${REAL_TOTAL} after scroll, got ${afterScroll.length}`
  );
  const ids = afterScroll.map((r) => r.threadId);
  check(
    'ui/no duplicate rows after paging',
    new Set(ids).size === ids.length,
    `${ids.length - new Set(ids).size} duplicate(s)`
  );

  await page.$eval('.sidebar-content', (el) => {
    el.scrollTop = el.scrollHeight;
  });
  const afterExhausted = await sidebarRows();
  check(
    'ui/scrolling past the end appends nothing',
    afterExhausted.length === REAL_TOTAL,
    `grew to ${afterExhausted.length}`
  );
  // Once the list is exhausted the affordance must be gone — a spinner that never resolves reads
  // to the user exactly like a list that is still loading more.
  const stillLoadingMore = await page.$('[data-testid="conversations-loading-more"]');
  check(
    'ui/the loading-more affordance clears once exhausted',
    stillLoadingMore === null,
    'the "Loading more" affordance is still present after the last page'
  );

  // ---------- UI: sort mode ----------
  await chooseSort('created');
  await waitForHead('Real conversation 45');
  const createdRows = await sidebarRows();
  check(
    'ui/switching to Created re-heads the list',
    (createdRows[0]?.title || '').includes('Real conversation 45'),
    `head is "${createdRows[0]?.title}"`
  );
  check(
    'ui/switching sort RESETS paging to one page',
    createdRows.length === PAGE_SIZE,
    `expected a reset to ${PAGE_SIZE}, got ${createdRows.length} - pages sorted two different ways must never be merged`
  );

  await chooseSort('lastUsed');
  await waitForHead('Real conversation 01');
  const backRows = await sidebarRows();
  check(
    'ui/switching back to Last used re-heads the list',
    (backRows[0]?.title || '').includes('Real conversation 01'),
    `head is "${backRows[0]?.title}"`
  );

  // ---------- UI: the sort choice survives a reload ----------
  await chooseSort('created');
  await waitForHead('Real conversation 45');
  await page.reload({ waitUntil: 'domcontentloaded' });
  await page.waitForSelector('[data-testid="conversation-item"]', { timeout: 30000 });
  const reloaded = await sidebarRows();
  check(
    'ui/the chosen sort mode survives a reload',
    (reloaded[0]?.title || '').includes('Real conversation 45'),
    `head after reload is "${reloaded[0]?.title}"`
  );

  return { pass: failures.length === 0, failures, steps };
}
