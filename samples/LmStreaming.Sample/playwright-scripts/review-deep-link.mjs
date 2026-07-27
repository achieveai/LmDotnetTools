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
  // The review is the newest workspace-agent conversation on the host. Deliberately NOT matched on title:
  // the daemon titles it from the agent profile, so a title match would couple this check to that wording.
  const isReview = (c) => String(c.mode ?? '') === 'workspace-agent';

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
      .filter(isReview)
      .sort((a, b) => Number(b.lastUpdated ?? 0) - Number(a.lastUpdated ?? 0));
    record('a hosted review conversation exists on the review host', reviews.length > 0, {
      total: items.length,
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
  } catch (e) {
    record('exception', false, String((e && e.stack) || e));
  }

  const failures = steps.filter((s) => !s.pass).map((s) => s.name);
  return { pass: failures.length === 0, failures, steps, link };
}
