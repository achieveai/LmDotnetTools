// focus-view.mjs — single-call Playwright check for the ?focus=1 read-focused deep-link view.
// Run the WHOLE flow in ONE call and get structured JSON back:
//
//   browser_run_code_unsafe({ filename: "samples/LmStreaming.Sample/playwright-scripts/focus-view.mjs" })
//
// Returns { pass, failures, steps }. Assert only DETERMINISTIC, browser-observable DOM state.
//
// Feature under test (ChatLayout.vue `focusMode`, gated on the ?focus=1 / ?focus=true query param):
//   WITH ?focus=1  → the review deep-link opens a read-focused single-conversation view: the left
//     `ConversationSidebar`, the header `.header-actions` cluster (Workspace/Provider/Mode pickers +
//     Marketplaces / Egress / Files / Clear buttons) and the sidebar-toggle `.menu-btn`s are all
//     HIDDEN, while the `.chat-header` title and the `message-list` read surface (plus, when present,
//     the sub-agent `ConversationTabs`) REMAIN — that is the whole point of the link.
//   WITHOUT ?focus=1 → the normal app renders the FULL chrome (sidebar + header-actions present),
//     proving the gate is opt-in and the default app is untouched.
//
// focusMode is purely query-driven (independent of any threadId), so `/?focus=1` with no thread is a
// clean, seed-free surface: notFoundThreadId stays null, so the main chat-view (header + MessageList)
// renders with the chrome stripped. No sandbox/gateway, provider, or seeded conversation is needed.
//
// BASE port: this run's server is started on :5057 (ASPNETCORE_URLS). Adjust if yours differs.
async (page) => {
  const BASE = 'http://localhost:5057';

  const steps = [];
  const record = (name, pass, detail) => steps.push({ name, pass, detail });
  // count() is 0 when the element is v-if'd out of the DOM entirely (how focusMode hides chrome).
  const present = async (selector) => (await page.locator(selector).count()) > 0;
  const visible = async (selector) => {
    const loc = page.locator(selector).first();
    return (await loc.count()) > 0 && (await loc.isVisible());
  };

  try {
    // ── 1. FOCUS MODE: /?focus=1 strips the app chrome ────────────────────────────────────────────
    await page.goto(`${BASE}/?focus=1`);
    await page.locator('[data-testid="chat-layout"]').waitFor({ timeout: 20000 });
    // The read surface must still mount — wait for it so we assert against a settled focus-mode DOM.
    await page.locator('[data-testid="message-list"]').waitFor({ state: 'visible', timeout: 20000 });

    const focus = {
      sidebar: await present('.conversation-sidebar'),
      headerActions: await present('.header-actions'),
      menuBtn: await present('.menu-btn'),
      headerTitle: await visible('.chat-header h1'),
      messageList: await visible('[data-testid="message-list"]'),
    };
    record('focus=1 hides the ConversationSidebar', !focus.sidebar, focus);
    record('focus=1 hides the header-actions cluster (pickers + action buttons)', !focus.headerActions, focus);
    record('focus=1 hides the sidebar-toggle menu-btn', !focus.menuBtn, focus);
    record('focus=1 keeps the chat-header title', focus.headerTitle, focus);
    record('focus=1 keeps the MessageList read surface', focus.messageList, focus);

    // ── 2. NORMAL MODE: / (no focus) keeps the FULL chrome (no regression to the default app) ──────
    await page.goto(`${BASE}/`);
    await page.locator('[data-testid="chat-layout"]').waitFor({ timeout: 20000 });
    // Header-actions only render outside focus mode — waiting for it also proves focus is opt-in.
    await page.locator('.header-actions').waitFor({ state: 'visible', timeout: 20000 });

    const normal = {
      sidebar: await present('.conversation-sidebar'),
      headerActions: await visible('.header-actions'),
      marketplaceBtn: await present('[data-testid="marketplace-button"]'),
      providerBtn: await present('[data-testid="provider-selector-button"]'),
    };
    record('no focus keeps the ConversationSidebar', normal.sidebar, normal);
    record('no focus keeps the header-actions cluster', normal.headerActions, normal);
    record('no focus keeps the marketplace + provider controls', normal.marketplaceBtn && normal.providerBtn, normal);
  } catch (e) {
    record('exception', false, String((e && e.stack) || e));
  }

  const failures = steps.filter((s) => !s.pass).map((s) => s.name);
  return { pass: failures.length === 0, failures, steps };
}
