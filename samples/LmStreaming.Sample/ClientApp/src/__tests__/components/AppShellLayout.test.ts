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

/**
 * Header-wrap regression guard (Clear-button clip).
 *
 * The header lays the title (`h1`, `flex: 1`) beside a right-aligned control row
 * (`.header-actions`: workspace + provider + mode selectors, "Marketplaces", "Clear").
 * On typical laptop widths those controls are collectively wider than the 900px content
 * column, so with a single non-wrapping flex row the trailing "Clear" button overflowed
 * the right edge and was clipped off-screen.
 *
 * The fix lets the row reflow: `.chat-header` wraps the control row below the title, and
 * `.header-actions` wraps its own buttons, so "Clear" always stays within the viewport.
 *
 * This is the fast structural guard (source-text) sitting under the behavioral
 * `ChatClientLayoutRegressionTests` Browser.E2E test, which actually renders the header at
 * 1280px and asserts the Clear button falls inside the viewport. happy-dom computes no
 * real layout, so the on-screen containment can only be proven in the C# Chromium suite;
 * this keeps a cheap, always-on check that the wrap declarations are not removed.
 */
describe('Header wrapping (Clear-button clip regression)', () => {
  const chatLayout = fs.readFileSync(
    path.resolve(__dirname, '../../components/ChatLayout.vue'),
    'utf-8'
  ) as string;

  it('lets .chat-header wrap so the control row drops below the title instead of clipping', () => {
    expect(chatLayout).toMatch(/\.chat-header\s*\{[^}]*flex-wrap:\s*wrap/);
  });

  it('lets .header-actions wrap its own controls rather than overflowing the row', () => {
    expect(chatLayout).toMatch(/\.header-actions\s*\{[^}]*flex-wrap:\s*wrap/);
  });
});
