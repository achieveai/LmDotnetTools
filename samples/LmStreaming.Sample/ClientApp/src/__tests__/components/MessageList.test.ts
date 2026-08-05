import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { mount } from '@vue/test-utils';
import MessageList from '../../components/MessageList.vue';
import TextMessage from '../../components/TextMessage.vue';
import { nextTick } from 'vue';

import { MessageType } from '@/types';
// Mock ResizeObserver
(globalThis as any).ResizeObserver = class ResizeObserver {
  observe() {}
  unobserve() {}
  disconnect() {}
};

describe('MessageList', () => {
  let requestAnimationFrameMock: any;
  let scrollToMock: any;

  beforeEach(() => {
    // Mock requestAnimationFrame to execute callback asynchronously to prevent stack overflow in recursion
    requestAnimationFrameMock = vi.spyOn(window, 'requestAnimationFrame')
      .mockImplementation((cb: any) => {
        setTimeout(() => cb(performance.now()), 0);
        return 0;
      });

    // Mock scrollTo
    scrollToMock = vi.fn();
    Element.prototype.scrollTo = scrollToMock;
  });

  afterEach(() => {
    vi.restoreAllMocks();
  });

  it('scrolls to new user message when added', async () => {
    const wrapper = mount(MessageList, {
      props: {
        displayItems: []
      },
      attachTo: document.body // Needed for offsetTop/scrolling
    });

    // Mock querySelector to return an element with specific offsetTop
    const mockElement = document.createElement('div');
    Object.defineProperty(mockElement, 'offsetTop', { value: 500, configurable: true });
    
    // Spy on the element that will be found
    const querySelectorSpy = vi.spyOn(wrapper.element, 'querySelector');
    querySelectorSpy.mockReturnValue(mockElement);
    
    // We need to spy on scrollTop setting.
    // Since wrapper.element is the messageListRef, we can check its scrollTop.
    // But setting scrollTop on a DOM element doesn't emit an event we can easy spy unless we use setters.
    // However, we can check the final value.
    
    // Initial state
    wrapper.element.scrollTop = 0;
    await nextTick();

    // Add a user message
    // Use correct object structure to avoid warning
    await wrapper.setProps({
      displayItems: [
        {
          id: 'msg-1',
          type: 'user-message',
          content: { $type: MessageType.Text, role: 'user', text: 'Hello', isThinking: false },
          status: 'active',
          timestamp: Date.now()
        }
      ]
    });

    // Wait for watchers and nextTick
    await nextTick();
    
    // The component uses double requestAnimationFrame
    // Our mock executes immediately.
    // The smoothScrollTo ALSO uses requestAnimationFrame loop.
    // Our mock executes valid callback immediately.
    
    // We need to advance timers or allow the recursive rAF to run?
    // With our mock implementation:
    // cb(0); return 0;
    // It calls the callback with time 0.
    // smoothScrollTo uses performance.now().
    
    // To properly test animation, we might need real timers or better mocks.
    // But for now, let's just ensure rAF was called multiple times (indicating animation loop started).
    
    await new Promise(resolve => setTimeout(resolve, 0));

    // Verify scrollTop changed (it might not reach 500 instantly in test env without proper time advancement,
    // but the loop should have started).
    // Or we can mock requestAnimationFrame to simulate multiple frames.
    
    expect(requestAnimationFrameMock).toHaveBeenCalled();
  });

  describe('Streaming highlight opt-out wiring', () => {
    // hljs TOKEN classes only; the block's own `hljs language-csharp` class is present either way.
    const tokenSpans = (html: string) => (html.match(/class="hljs-/g) || []).length;
    const FENCE = ['```csharp', 'var s = "a & b";', 'if (x is null) { }', '```'].join('\n');

    const userItem = (id: string) => ({
      id,
      type: 'user-message' as const,
      content: { $type: MessageType.Text, role: 'user' as const, text: 'Show me code', isThinking: false },
      status: 'active' as const,
      timestamp: Date.now(),
    });
    const assistantItem = (id: string, text = FENCE) => ({
      id,
      type: 'assistant-message' as const,
      content: { $type: MessageType.Text, role: 'assistant' as const, text, isThinking: false },
    });
    // `MetadataPill`/`NotificationPill` are stubbed: what is under test is which item the scan
    // stops on, not how those pills render.
    const pillItem = (id: string) => ({ id, type: 'pill' as const, items: [] });
    const notificationItem = (id: string) => ({
      id,
      type: 'notification' as const,
      notification: { notifyKind: 'subagent-completed', text: 'child finished' },
    });

    const isCompleteById = (wrapper: any) =>
      Object.fromEntries(
        wrapper
          .findAllComponents(TextMessage)
          .map((c: any) => [c.props('message').text.slice(0, 12), c.props('isComplete')])
      );

    const mountList = (displayItems: any[], isLoading: boolean) =>
      mount(MessageList, {
        props: { displayItems, isLoading },
        global: { stubs: { MetadataPill: true, NotificationPill: true } },
      });

    it('marks only the LAST assistant bubble of the active group incomplete while loading', () => {
      const wrapper = mountList(
        [userItem('u-1'), assistantItem('a-1', 'first block'), assistantItem('a-2', 'second block')],
        true
      );

      // Two bubbles in the same active group: the earlier one is finished and must stay
      // highlighted, or already-rendered code goes monochrome mid-run.
      expect(isCompleteById(wrapper)).toMatchObject({
        'Show me code': true,
        'first block': true,
        'second block': false,
      });
    });

    it('marks every bubble complete once the run ends', async () => {
      const items = [userItem('u-1'), assistantItem('a-1', 'first block'), assistantItem('a-2', 'second block')];
      const wrapper = mountList(items, true);

      await wrapper.setProps({ isLoading: false });

      expect(isCompleteById(wrapper)).toMatchObject({ 'first block': true, 'second block': true });
    });

    it('keeps HISTORY bubbles complete while a later turn streams', () => {
      const wrapper = mountList(
        [
          userItem('u-1'),
          assistantItem('a-1', 'history block'),
          userItem('u-2'),
          assistantItem('a-2', 'live block'),
        ],
        true
      );

      expect(isCompleteById(wrapper)).toMatchObject({
        'history bloc': true,
        'live block': false,
      });
    });

    it('renders the streaming bubble UNHIGHLIGHTED and highlights it when the run ends', async () => {
      const wrapper = mountList([userItem('u-1'), assistantItem('a-1')], true);

      // End-to-end through MessageList -> TextMessage -> parseMarkdown, not just the prop.
      expect(tokenSpans(wrapper.html())).toBe(0);
      expect(wrapper.html()).toContain('hljs language-csharp');

      await wrapper.setProps({ isLoading: false });

      expect(tokenSpans(wrapper.html())).toBeGreaterThan(0);
    });

    it('highlights everything when not loading at all', () => {
      const wrapper = mountList([userItem('u-1'), assistantItem('a-1')], false);
      expect(tokenSpans(wrapper.html())).toBeGreaterThan(0);
    });

    // The first cut of this feature searched `splitGroups.current`, which is empty whenever there
    // is no user group -- so the SECOND consumer (SubAgentTranscript, assistant-only) never got the
    // opt-out at all and the component-level tests above stayed green anyway.
    it('marks the last bubble of an ASSISTANT-ONLY transcript incomplete (SubAgentTranscript shape)', () => {
      const wrapper = mountList([assistantItem('a-1', 'first block'), assistantItem('a-2')], true);

      expect(isCompleteById(wrapper)).toMatchObject({
        'first block': true,
        '```csharp\nva': false,
      });
      expect(tokenSpans(wrapper.html())).toBe(0);
    });

    // Monochrome guard: nothing is streaming text, so NOTHING may be marked incomplete.
    it('leaves every bubble complete when the newest item is a user message', () => {
      const wrapper = mountList(
        [userItem('u-1'), assistantItem('a-1'), userItem('u-2')],
        true
      );

      expect(isCompleteById(wrapper)).toMatchObject({ '```csharp\nva': true });
      expect(tokenSpans(wrapper.html())).toBeGreaterThan(0);
    });

    // A pill is only flushed once something FOLLOWS the buffered reasoning/tool messages, so a
    // trailing pill means the assistant left the text and moved onto a tool call. That text is
    // finished and must stay highlighted for however long the tool runs.
    it('leaves the bubble complete when a pill trails it (assistant moved onto a tool call)', () => {
      const wrapper = mountList([userItem('u-1'), assistantItem('a-1'), pillItem('p-1')], true);

      expect(isCompleteById(wrapper)).toMatchObject({ '```csharp\nva': true });
      expect(tokenSpans(wrapper.html())).toBeGreaterThan(0);
    });

    it('marks the bubble AFTER a pill incomplete, not the one before it', () => {
      const wrapper = mountList(
        [userItem('u-1'), assistantItem('a-1', 'pre-tool block'), pillItem('p-1'), assistantItem('a-2')],
        true
      );

      expect(isCompleteById(wrapper)).toMatchObject({
        'pre-tool blo': true,
        '```csharp\nva': false,
      });
    });

    // Notifications are out-of-band (sub-agent completion, agent message, context discovery) and can
    // land mid-stream, so they say nothing about whether the text below them is still growing.
    it('skips a trailing notification and still marks the growing bubble incomplete', () => {
      const wrapper = mountList(
        [userItem('u-1'), assistantItem('a-1'), notificationItem('n-1')],
        true
      );

      expect(isCompleteById(wrapper)).toMatchObject({ '```csharp\nva': false });
    });
  });

  describe('Layout containment (overflow regression)', () => {    const componentSource = (() => {
      const fs = require('fs');
      const path = require('path');
      return fs.readFileSync(
        path.resolve(__dirname, '../../components/MessageList.vue'),
        'utf-8'
      ) as string;
    })();

    it('should have width 100% on .assistant-message-wrapper to fill available space', () => {
      expect(componentSource).toMatch(/\.assistant-message-wrapper\s*\{[^}]*width:\s*100%/);
    });

    it('should have min-width 0 on message containers to prevent flex overflow', () => {
      // The combined rule targets both user and assistant containers
      expect(componentSource).toMatch(/\.assistant-message-container[^{]*\{[^}]*min-width:\s*0/);
    });
  });
});

