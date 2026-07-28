// review-deep-link.mjs — single-call Playwright check that a Revobot review deep-link OPENS the real
// hosted review conversation in the read-focused view. Run the WHOLE flow in ONE call:
//
//   browser_run_code_unsafe({ filename: "samples/LmStreaming.Sample/playwright-scripts/review-deep-link.mjs" })
//
// Returns { pass, failures, steps, link }.
//
// This is the browser half of the CodeReviewDaemon S2S bring-up (plan gate G9). The daemon posts
// `{lmStreamingBaseUrl}/?threadId={threadId}&focus=1` on the PR; this script proves that exact URL lands on
// the review conversation with the chrome stripped and the sub-agent surface still reachable.
//
// Unlike focus-view.mjs (which asserts the chrome gate on a thread-less `/?focus=1`), this drives a REAL
// hosted thread: it discovers the review conversation from `/api/conversations` rather than hardcoding an
// id, so it keeps working across daemon runs. BASE is the review host started for the bring-up (:5051) —
// deliberately NOT the production instance on :5050.
async (page) => {
  const BASE = 'http://localhost:5051';
  // Set to the threadId out of a specific posted comment to verify THAT link rather than whichever
  // review ran most recently. Leave null for the normal "latest review" sweep: with the daemon still
  // polling, the newest review conversation belongs to whatever PR it picked up last, which is not
  // necessarily the one whose comment you are holding.
  const PIN_THREAD_ID = null;
  // The review arm is identified by its TITLE, not by being the newest workspace-agent conversation.
  // The daemon titles every hosted conversation `Review PR #{n} — {agent profile name}`
  // (S2SReviewAgentLoopFactory.BuildTitle), and the judge / knowledge-extraction arms run as SEPARATE
  // conversations against the SAME workspace in the SAME workspace-agent mode. The judge is provisioned
  // LAST, so "newest workspace-agent" selects the judge — whose title also carries `#{n}`, so every
  // PR-number assertion below still passed while reading the wrong thread, and only the sub-agent
  // assertions failed (a one-turn judge dispatches none), pointing at the wrong cause. Any ad-hoc
  // conversation opened against the host by hand is excluded the same way. Matched on the arm name
  // rather than the em dash so the check does not hinge on that character surviving a round-trip.
  const isReview = (c) =>
    String(c.mode ?? '') === 'workspace-agent' &&
    /^Review PR #\d+/.test(String(c.title ?? '')) &&
    !/Judge|Knowledge/.test(String(c.title ?? ''));

  const steps = [];
  const record = (name, pass, detail) => steps.push({ name, pass, detail });
  const present = async (selector) => (await page.locator(selector).count()) > 0;
  const visible = async (selector) => {
    const loc = page.locator(selector).first();
    return (await loc.count()) > 0 && (await loc.isVisible());
  };

  let link = null;

  try {
    // ── 1. Discover the review thread the daemon provisioned (newest matching title) ────────────────
    await page.goto(`${BASE}/`);
    const conversations = await page.evaluate(async () => {
      const res = await fetch('/api/conversations');
      if (!res.ok) return { error: `GET /api/conversations -> ${res.status}` };
      return { items: await res.json() };
    });

    if (conversations.error) {
      record('list conversations', false, conversations);
      return { pass: false, failures: ['list conversations'], steps, link };
    }

    const items = Array.isArray(conversations.items) ? conversations.items : [];
    const reviews = items
      .filter((c) => (PIN_THREAD_ID ? String(c.threadId) === PIN_THREAD_ID : isReview(c)))
      .sort((a, b) => Number(b.lastUpdated ?? 0) - Number(a.lastUpdated ?? 0));
    record('a hosted review conversation exists on the review host', reviews.length > 0, {
      total: items.length,
      pinned: PIN_THREAD_ID,
      reviews: reviews.map((c) => ({ threadId: c.threadId, title: c.title, mode: c.mode })),
    });
    if (reviews.length === 0) {
      return { pass: false, failures: ['a hosted review conversation exists on the review host'], steps, link };
    }

    const review = reviews[0];
    link = `${BASE}/?threadId=${review.threadId}&focus=1`;
    // The PR number the daemon put in the title ("Review PR #222 — …"). Focus mode strips every picker
    // and the sidebar, so this header is the ONLY thing telling a judge which PR they are looking at —
    // asserting merely that it is non-empty would pass on a placeholder or a neighbouring review.
    const prNumber = (String(review.title ?? '').match(/#(\d+)/) ?? [])[1] ?? null;

    // ── 2. Follow the posted deep-link ─────────────────────────────────────────────────────────────
    await page.goto(link);
    await page.locator('[data-testid="chat-layout"]').waitFor({ timeout: 20000 });
    await page.locator('[data-testid="message-list"]').waitFor({ state: 'visible', timeout: 20000 });
    // The deep-linked thread loads its history asynchronously; wait for content, not a fixed delay.
    await page
      .locator('[data-testid="user-message-group"], [data-testid="assistant-message-group"]')
      .first()
      .waitFor({ state: 'visible', timeout: 30000 })
      .catch(() => {});

    const headerTitle = (await page.locator('.chat-header h1').first().textContent().catch(() => '')) ?? '';
    const groups = await page.locator('[data-testid="assistant-message-group"]').count();
    const opened = {
      threadId: review.threadId,
      listedTitle: String(review.title ?? ''),
      prNumber,
      headerTitle: headerTitle.trim(),
      assistantGroups: groups,
      sidebar: await present('.conversation-sidebar'),
      headerActions: await present('.header-actions'),
      menuBtn: await present('.menu-btn'),
      messageList: await visible('[data-testid="message-list"]'),
      subagentToggle: await present('[data-testid="subagent-panel-toggle"]'),
      conversationTabs: await present('[data-testid="conversation-tabs"]'),
    };

    record(
      'the review conversation is titled with its PR number',
      prNumber !== null,
      opened);
    record(
      'deep-link opens the deep-linked thread (header names that PR)',
      prNumber !== null && opened.headerTitle.includes(`#${prNumber}`),
      opened);
    record('deep-link renders the hosted review transcript', opened.assistantGroups > 0, opened);
    record('focus=1 hides the ConversationSidebar', !opened.sidebar, opened);
    record('focus=1 hides the header-actions cluster', !opened.headerActions, opened);
    record('focus=1 hides the sidebar-toggle menu-btn', !opened.menuBtn, opened);
    record('focus=1 keeps the MessageList read surface', opened.messageList, opened);
    // The sub-agent surface is the reason the link exists: the panel toggle must survive focus mode.
    record('focus=1 keeps the sub-agent panel reachable', opened.subagentToggle, opened);

    // ── 3. The panel must name the REVIEWERS that ran (plan gate G6) ───────────────────────────────
    // An empty or generic panel is a fail: the point of the link is that a judge can read what each
    // code-reviewer:* sub-agent actually did. This is the half that regressed whenever the roster was
    // resolved from live pool state only — a finished review's parent is no longer pooled.
    if (opened.subagentToggle) {
      await page.locator('[data-testid="subagent-panel-toggle"]').first().click();
      await page
        .locator('[data-testid="subagent-item"]')
        .first()
        .waitFor({ state: 'visible', timeout: 15000 })
        .catch(() => {});

      const children = await page.evaluate(() =>
        [...document.querySelectorAll('[data-testid="subagent-item"]')].map((n) => ({
          agentId: n.getAttribute('data-agent-id'),
          text: (n.textContent ?? '').replace(/\s+/g, ' ').trim(),
        })));
      record('the sub-agent panel lists the children that ran', children.length > 0, { children });

      // The panel renders name + task + status, NOT the template id — so "are these the reviewers?"
      // has to be answered from the listing API, then cross-checked against what the DOM actually
      // rendered. A generic roster (general-purpose/researcher only) means the workspace marketplace
      // never reached the gateway.
      const listed = await page.evaluate(async (threadId) => {
        const res = await fetch(`/api/conversations/${threadId}/subagents`);
        if (!res.ok) return { error: `GET subagents -> ${res.status}` };
        return { items: await res.json() };
      }, review.threadId);
      const api = Array.isArray(listed.items) ? listed.items : [];
      const reviewers = api.filter((c) => String(c.template ?? '').startsWith('code-reviewer:'));
      const domIds = new Set(children.map((c) => c.agentId));
      const missingFromDom = api.filter((c) => !domIds.has(c.agentId)).map((c) => c.agentId);
      record(
        'the listed sub-agents are the code-reviewer:* reviewers',
        reviewers.length > 0 && missingFromDom.length === 0,
        {
          listedError: listed.error ?? null,
          templates: api.map((c) => c.template),
          reviewers: reviewers.length,
          domCount: children.length,
          missingFromDom,
        });

      // Opening one must render ITS transcript, not an empty shell — that is what a judge reads.
      if (children.length > 0) {
        await page.locator('[data-testid="subagent-focus-button"]').first().click();
        const transcript = await page
          .locator('[data-testid="subagent-transcript"]')
          .first()
          .waitFor({ state: 'visible', timeout: 20000 })
          .then(() => true)
          .catch(() => false);
        // The child's transcript is a MessageList, so its content is the usual message groups. The
        // container renders as soon as the child is focused but its history is fetched afterwards —
        // wait for content rather than counting into the load window.
        const groupsLocator = page.locator(
          '[data-testid="subagent-transcript"] [data-testid="assistant-message-group"], ' +
            '[data-testid="subagent-transcript"] [data-testid="user-message-group"]');
        await groupsLocator.first().waitFor({ state: 'visible', timeout: 30000 }).catch(() => {});
        const items = await groupsLocator.count().catch(() => 0);
        const childError = await page
          .locator('[data-testid="subagent-error"]')
          .first()
          .textContent()
          .catch(() => null);
        record('a focused sub-agent renders its persisted transcript', transcript && items > 0, {
          transcript,
          items,
          // A dead parent yields a `subagent_unavailable` LIVE-connection error frame; the persisted
          // transcript still renders beneath it, so this is reported, not asserted on.
          childError: childError?.trim() ?? null,
        });
      }
    }
  } catch (e) {
    record('exception', false, String((e && e.stack) || e));
  }

  const failures = steps.filter((s) => !s.pass).map((s) => s.name);
  return { pass: failures.length === 0, failures, steps, link };
}
