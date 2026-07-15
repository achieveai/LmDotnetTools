import { describe, it, expect } from 'vitest';
import fs from 'fs';
import path from 'path';

/**
 * App-shell / conversation-scroll regression guard.
 *
 * The chat client is a FIXED full-height shell: the page/body must never scroll —
 * only the conversation list (`.message-list`) scrolls, internally.
 *
 * A whole-page scrollbar appeared once (alongside the correct conversation
 * scrollbar) because the tool pills render `.sr-only` accessibility labels with
 * `position: absolute`, and EVERY ancestor up to the document was `position: static`.
 * With no positioned ancestor, those labels' containing block resolved to the
 * initial containing block (the document), so `.chat-layout { overflow: hidden }`
 * could NOT clip them — they sat at their in-flow depth (below the fold of the
 * scrolled conversation) and lengthened `documentElement` into a spurious page
 * scrollbar. `overflow: hidden` only clips descendants whose containing block is
 * that element, so clipping an ancestor was not enough.
 *
 * The fix: `.tool-pill` is `position: relative`, becoming the containing block for
 * its own absolutely-positioned `.sr-only` labels so they stay inside the pill and
 * are absorbed by `.message-list`'s internal scroll instead of the page.
 *
 * The invariants below must hold together:
 *   1. `.tool-pill`   → `position: relative` (contains its abs `.sr-only` labels).
 *   2. `.chat-layout` → `height: 100vh` + `overflow: hidden` (caps the shell).
 *   3. `.message-list`→ `overflow-y: auto` (the single intended scroll region).
 */
describe('Conversation-scroll containment (page-scroll regression)', () => {
  const toolPill = fs.readFileSync(
    path.resolve(__dirname, '../../components/ToolPill.vue'),
    'utf-8'
  ) as string;

  const chatLayout = fs.readFileSync(
    path.resolve(__dirname, '../../components/ChatLayout.vue'),
    'utf-8'
  ) as string;

  const messageList = fs.readFileSync(
    path.resolve(__dirname, '../../components/MessageList.vue'),
    'utf-8'
  ) as string;

  it('makes .tool-pill a positioning context so its absolute .sr-only labels stay contained', () => {
    // The `.sr-only` labels are absolutely positioned; the pill must be their containing block.
    expect(toolPill).toMatch(/\.tool-pill\s*\{[^}]*position:\s*relative/);
    // Guard that the leaking element is still the absolutely-positioned .sr-only (fix stays relevant).
    expect(toolPill).toMatch(/\.sr-only\s*\{[^}]*position:\s*absolute/);
  });

  it('keeps the .chat-layout shell height-capped and clipped', () => {
    expect(chatLayout).toMatch(/\.chat-layout\s*\{[^}]*height:\s*100vh/);
    expect(chatLayout).toMatch(/\.chat-layout\s*\{[^}]*overflow:\s*hidden/);
  });

  it('keeps .message-list as the single internal scroll region', () => {
    expect(messageList).toMatch(/\.message-list\s*\{[^}]*overflow-y:\s*auto/);
  });
});
