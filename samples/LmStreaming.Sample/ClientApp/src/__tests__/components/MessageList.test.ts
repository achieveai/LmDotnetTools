import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { mount } from '@vue/test-utils';
import MessageList from '../../components/MessageList.vue';
import TextMessage from '../../components/TextMessage.vue';
import CopyMessageButton from '../../components/CopyMessageButton.vue';
import { nextTick } from 'vue';

import { MessageType } from '@/types';
import { GET_RESULT_FOR_TOOL_CALL } from '@/composables/useToolResult';
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

  it('labels message groups while keeping the quiet role marks decorative in history and the active turn', () => {
    const text = (role: 'user' | 'assistant', value: string) => ({
      $type: MessageType.Text,
      role,
      text: value,
      isThinking: false,
    });
    const wrapper = mount(MessageList, {
      props: {
        displayItems: [
          { id: 'u-1', type: 'user-message', content: text('user', 'First'), status: 'active', timestamp: 1 },
          { id: 'a-1', type: 'assistant-message', content: text('assistant', 'First answer') },
          { id: 'u-2', type: 'user-message', content: text('user', 'Second'), status: 'active', timestamp: 2 },
          { id: 'a-2', type: 'assistant-message', content: text('assistant', 'Second answer') },
        ],
      },
    });

    expect(wrapper.findAll('[role="group"][aria-label="Your message"]')).toHaveLength(2);
    expect(wrapper.findAll('[role="group"][aria-label="Assistant message"]')).toHaveLength(2);
    expect(wrapper.findAll('[data-testid="message-role-mark"]').map((mark) => mark.text())).toEqual([
      'You',
      'AI',
      'You',
      'AI',
    ]);
    expect(
      wrapper.findAll('[data-testid="message-role-mark"]').every((mark) => mark.attributes('aria-hidden') === 'true')
    ).toBe(true);
  });

  it('uses PendingMessage as the only surface for queued messages in history and the active turn', () => {
    const text = (role: 'user' | 'assistant', value: string) => ({
      $type: MessageType.Text,
      role,
      text: value,
      isThinking: false,
    });
    const wrapper = mount(MessageList, {
      props: {
        displayItems: [
          { id: 'u-1', type: 'user-message', content: text('user', 'Queued earlier'), status: 'pending', timestamp: 1 },
          { id: 'a-1', type: 'assistant-message', content: text('assistant', 'Answer') },
          { id: 'u-2', type: 'user-message', content: text('user', 'Queued now'), status: 'pending', timestamp: 2 },
        ],
      },
    });

    const userContents = wrapper.findAll('.user-content');
    expect(userContents).toHaveLength(2);
    expect(userContents.every((content) => !content.classes('user-content-surface'))).toBe(true);
    const pendingMessages = wrapper.findAll('.pending-message');
    expect(pendingMessages).toHaveLength(2);
    expect(
      pendingMessages.every((pending) => pending.element.closest('.user-message-wrapper') !== null)
    ).toBe(true);
    expect(wrapper.findAll('.waiting-indicator')).toHaveLength(2);
  });

  it('keeps prose and rich answer blocks in the same full-width answer row', () => {
    const wrapper = mount(MessageList, {
      props: {
        displayItems: [
          {
            id: 'u-1',
            type: 'user-message',
            content: { $type: MessageType.Text, role: 'user', text: 'Show the report', isThinking: false },
            status: 'active',
            timestamp: 1,
          },
          {
            id: 'a-1',
            type: 'assistant-message',
            content: {
              $type: MessageType.Text,
              role: 'assistant',
              text: '# Report\n\nReadable paragraph.\n\n```text\nwide output\n```\n\n| A | B |\n| - | - |\n| 1 | 2 |',
              isThinking: false,
            },
          },
        ],
      },
      attachTo: document.body,
    });

    const row = wrapper.get('.text-bubble-row');
    const markdown = wrapper.get('[data-testid="assistant-text"] .markdown-content');
    const prose = markdown.get('p').element;
    const richBlocks = ['h1', 'pre', 'table'].map((selector) => markdown.get(selector).element);
    expect(prose.matches('.markdown-content > :is(p, ul, ol, blockquote)')).toBe(true);
    expect(richBlocks.every((block) => !block.matches('.markdown-content > :is(p, ul, ol, blockquote)'))).toBe(true);
    expect([prose, ...richBlocks].every((block) => block.closest('.text-bubble-row') === row.element)).toBe(true);
    expect(row.find('.bubble-copy').exists()).toBe(true);
    wrapper.unmount();
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

    it('should have min-width 0 on message containers to prevent flex overflow', () => {
      // The combined rule targets both user and assistant containers
      expect(componentSource).toMatch(/\.assistant-message-container[^{]*\{[^}]*min-width:\s*0/);
    });

    it('lets assistant markdown fill its row while user messages retain their compact bubble', () => {
      expect(componentSource).toMatch(
        /\.assistant-message-wrapper\s*\{[^}]*width:\s*100%[^}]*max-width:\s*100%/
      );
      expect(componentSource).toMatch(
        /\.user-message-wrapper\s*\{[^}]*margin-left:\s*auto[^}]*max-width:\s*70%/
      );
      expect(componentSource).toMatch(
        /\.assistant-content\s+:deep\(\.text-message\)\s*,\s*\.assistant-content\s+:deep\(\.markdown-content\)\s*\{[^}]*width:\s*100%[^}]*max-width:\s*100%[^}]*min-width:\s*0/
      );
      expect(componentSource).toMatch(
        /\.assistant-content\s+:deep\(\.markdown-content table\)\s*\{[^}]*width:\s*100%/
      );
      expect(componentSource).toMatch(
        /\.text-bubble\s*\{[^}]*width:\s*100%[^}]*max-width:\s*100%[^}]*min-width:\s*0[^}]*box-sizing:\s*border-box/
      );
      expect(componentSource).not.toMatch(
        /\.text-bubble\s+:deep\(\.markdown-content\s*>\s*:is\(p,\s*ul,\s*ol,\s*blockquote\)\)\s*\{[^}]*max-width/
      );
    });
  });
});

describe('MessageList Consumer activity projection', () => {
  const user = (id: string, text: string) => ({
    id,
    type: 'user-message' as const,
    content: { $type: MessageType.Text, role: 'user' as const, text },
    status: 'active' as const,
    timestamp: Date.now(),
  });
  const answer = (id: string, text: string, runId = 'run-1', isThinking = false) => ({
    id,
    type: 'assistant-message' as const,
    content: { $type: MessageType.Text, role: 'assistant' as const, text, isThinking },
    runId,
  });
  const pill = (id: string, runId = 'run-1') => ({
    id,
    type: 'pill' as const,
    runId,
    items: [
      { $type: MessageType.Reasoning, role: 'assistant' as const, reasoning: 'private analysis' },
      {
        $type: MessageType.ToolsCall,
        role: 'assistant' as const,
        tool_calls: [{ tool_call_id: 'call-1', function_name: 'Read', function_args: '{"path":"a.md"}' }],
      },
    ],
  });
  const notice = (id: string, notifyKind: string, runId = 'run-1') => ({
    id,
    type: 'notification' as const,
    runId,
    notification: { notifyKind, label: `${notifyKind} label`, detail: `${notifyKind} detail` },
  });

  function mountList(
    displayItems: any[],
    options: { isLoading?: boolean; result?: any } = {}
  ) {
    return mount(MessageList, {
      props: { displayItems, isLoading: options.isLoading ?? false, viewPreference: 'consumer' },
      global: {
        provide: {
          [GET_RESULT_FOR_TOOL_CALL]: () => options.result ?? null,
        },
      },
    });
  }

  it('keeps every user and answer block but replaces a turn’s reasoning and tools with one activity line', () => {
    const wrapper = mountList([
      user('u-1', 'Question'),
      answer('thinking-1', 'hidden thought', 'run-1', true),
      answer('a-1', 'First answer'),
      pill('p-1'),
      answer('a-2', 'Second answer'),
    ]);

    expect(wrapper.text()).toContain('Question');
    expect(wrapper.findAll('[data-testid="assistant-text"]').map((node) => node.text())).toEqual([
      'First answer',
      'Second answer',
    ]);
    expect(wrapper.findAll('[data-testid="turn-activity"]')).toHaveLength(1);
    expect(wrapper.text()).not.toContain('hidden thought');
    expect(wrapper.find('[data-testid="metadata-pill"]').exists()).toBe(false);
  });

  it('creates one activity line for each user turn', () => {
    const wrapper = mountList([
      user('u-1', 'First question'),
      pill('p-1', 'run-1'),
      answer('a-1', 'First answer', 'run-1'),
      user('u-2', 'Second question'),
      pill('p-2', 'run-2'),
      answer('a-2', 'Second answer', 'run-2'),
    ]);

    expect(wrapper.findAll('[data-testid="turn-activity"]')).toHaveLength(2);
    expect(wrapper.findAll('[data-testid="assistant-text"]').map((node) => node.text())).toEqual([
      'First answer',
      'Second answer',
    ]);
  });

  it('reveals the unchanged rich activity details on demand', async () => {
    const wrapper = mountList([user('u-1', 'Question'), pill('p-1'), notice('compact', 'compaction')]);

    const toggle = wrapper.get('[data-testid="turn-activity-toggle"]');
    expect(toggle.attributes('aria-expanded')).toBe('false');
    expect(toggle.attributes('aria-controls')).toBeTruthy();
    await toggle.trigger('click');

    expect(toggle.attributes('aria-expanded')).toBe('true');
    expect(wrapper.getComponent({ name: 'MetadataPill' }).props('presentation')).toBe('activity-row');
    wrapper.get('[data-notify-kind="compaction"]');
  });

  it('keeps generic and descendant-question notices visible outside activity', () => {
    const wrapper = mountList([
      user('u-1', 'Question'),
      pill('p-1'),
      notice('generic', 'client-notification'),
      notice('question', 'descendant-question'),
    ]);

    expect(wrapper.findAll('[data-testid="turn-activity"]')).toHaveLength(1);
    wrapper.get('[data-notify-kind="client-notification"]');
    wrapper.get('[data-notify-kind="descendant-question"]');
  });

  it('folds an agent-directed todo nudge into activity with a neutral headline', async () => {
    const internalNotice = notice('internal', 'todo-nudge');
    internalNotice.notification.detail = 'Claim assigned task 4';
    const wrapper = mountList([user('u-1', 'Question'), internalNotice]);

    expect(wrapper.find('[data-notify-kind="todo-nudge"]').exists()).toBe(false);
    expect(wrapper.get('[data-testid="turn-activity-toggle"]').text()).toContain('Work reminder');
    await wrapper.get('[data-testid="turn-activity-toggle"]').trigger('click');
    wrapper.get('[data-notify-kind="todo-nudge"]');
  });

  it('keeps workflow completion standalone because its opaque detail may contain a failure', () => {
    const workflow = notice('workflow', 'workflow-completion');
    workflow.notification.detail = '<workflow id="wf-1" status="failed">Error: stopped</workflow>';
    const wrapper = mountList([user('u-1', 'Question'), workflow]);

    expect(wrapper.find('[data-testid="turn-activity"]').exists()).toBe(false);
    wrapper.get('[data-notify-kind="workflow-completion"]');
  });

  it('shows failure in the collapsed headline', () => {
    const result = { result: 'boom', is_error: true };
    const wrapper = mountList([user('u-1', 'Question'), pill('p-1')], { result });

    expect(wrapper.get('[data-testid="turn-activity-toggle"]').text()).toContain('failed');
  });

  it('surfaces a deferred question as waiting for the user', () => {
    const result = { result: '', is_deferred: true };
    const wrapper = mountList([user('u-1', 'Question'), pill('p-1')], {
      isLoading: true,
      result,
    });

    expect(wrapper.get('[data-testid="turn-activity-toggle"]').text()).toContain(
      'Waiting for your answer'
    );
  });

  it('updates one Working line in place while a tool is active', async () => {
    const items = [user('u-1', 'Question'), pill('p-1')];
    const wrapper = mountList(items, { isLoading: true });
    const activity = wrapper.get('[data-testid="turn-activity"]');
    expect(activity.text()).toContain('Working');

    await wrapper.setProps({ displayItems: [...items, answer('a-1', 'Partial answer')] });
    expect(wrapper.findAll('[data-testid="turn-activity"]')).toHaveLength(1);
    expect(wrapper.get('[data-testid="turn-activity"]').element).toBe(activity.element);
  });

  it('uses Working for reasoning-only activity while the run is live', () => {
    const wrapper = mountList(
      [user('u-1', 'Question'), answer('thinking-1', 'analysis', 'run-1', true)],
      { isLoading: true }
    );

    expect(wrapper.get('[data-testid="turn-activity-toggle"]').text()).toContain('Working');
  });

  it('stays Working after a tool succeeds while the same run is still producing text', () => {
    const wrapper = mountList(
      [user('u-1', 'Question'), pill('p-1'), answer('a-1', 'Still writing')],
      { isLoading: true, result: { result: 'done', is_error: false } }
    );

    expect(wrapper.get('[data-testid="turn-activity-toggle"]').text()).toContain('Working');
  });

  it('does not relabel old activity as Working when a new user turn has no response yet', () => {
    const wrapper = mountList(
      [user('u-1', 'First'), pill('p-1'), user('u-2', 'Second')],
      { isLoading: true }
    );

    expect(wrapper.get('[data-testid="turn-activity-toggle"]').text()).toContain('Activity');
    expect(wrapper.get('[data-testid="turn-activity-toggle"]').text()).not.toContain('Working');
  });

  it('does not relabel old runless activity as Working after a new user turn', () => {
    const legacyPill = { ...pill('p-legacy'), runId: undefined };
    const wrapper = mountList(
      [user('u-1', 'First'), legacyPill, user('u-2', 'Second')],
      { isLoading: true }
    );

    expect(wrapper.get('[data-testid="turn-activity-toggle"]').text()).toContain('Activity');
    expect(wrapper.get('[data-testid="turn-activity-toggle"]').text()).not.toContain('Working');
  });

  it('ignores a trailing out-of-band notice with no run id when identifying the live run', () => {
    const trailing = { ...notice('n-1', 'client-notification'), runId: undefined };
    const wrapper = mountList(
      [user('u-1', 'Question'), pill('p-1'), trailing],
      { isLoading: true }
    );

    expect(wrapper.get('[data-testid="turn-activity-toggle"]').text()).toContain('Working');
  });

  it('keeps agent delivery failures visible outside collapsed activity', () => {
    const failedDelivery = {
      ...notice('n-1', 'agent-message'),
      notification: {
        notifyKind: 'agent-message',
        agentMessageType: 'DeliveryFailure',
        label: 'Worker',
        detail: 'Message could not be delivered',
      },
    };
    const wrapper = mountList([user('u-1', 'Question'), pill('p-1'), failedDelivery]);

    wrapper.get('[data-notify-kind="agent-message"]');
    expect(wrapper.text()).toContain('Message undelivered');
  });

  it('uses activity rows for Developer tool details in history and the current turn', () => {
    const wrapper = mount(MessageList, {
      props: {
        displayItems: [
          user('u-1', 'First question'),
          pill('p-1', 'run-1'),
          answer('a-1', 'First answer', 'run-1'),
          user('u-2', 'Second question'),
          pill('p-2', 'run-2'),
        ],
        viewPreference: 'developer',
      },
    });

    expect(wrapper.find('[data-testid="turn-activity"]').exists()).toBe(false);
    expect(
      wrapper.findAllComponents({ name: 'MetadataPill' }).map((pill) => pill.props('presentation'))
    ).toEqual(['activity-row', 'activity-row']);
  });

  it('keeps the first visible answer anchored when activity collapses', async () => {
    const items = [user('u-1', 'Question'), pill('p-1'), answer('a-1', 'Visible answer')];
    const wrapper = mount(MessageList, {
      props: { displayItems: items, viewPreference: 'developer' },
    });
    const list = wrapper.get('[data-testid="message-list"]').element as HTMLElement;
    list.scrollTop = 300;
    vi.spyOn(list, 'getBoundingClientRect').mockReturnValue({ top: 0 } as DOMRect);
    const userAnchor = wrapper.get('[data-view-anchor="u-1"]').element as HTMLElement;
    vi.spyOn(userAnchor, 'getBoundingClientRect').mockReturnValue({ top: -80, bottom: -20 } as DOMRect);
    const answerAnchor = wrapper.get('[data-view-anchor="a-1"]').element as HTMLElement;
    let answerRectRead = 0;
    vi.spyOn(answerAnchor, 'getBoundingClientRect').mockImplementation(() => {
      answerRectRead += 1;
      return { top: answerRectRead <= 2 ? 50 : 10, bottom: 80 } as DOMRect;
    });

    await wrapper.setProps({ viewPreference: 'consumer' });
    await nextTick();

    expect(list.scrollTop).toBe(260);
    expect(wrapper.get('[data-view-anchor="a-1"]').text()).toContain('Visible answer');
  });
});



describe('MessageList copy button and workspace links', () => {
  const userItem = (id: string, text = 'hi') => ({
    id,
    type: 'user-message' as const,
    content: { $type: MessageType.Text, role: 'user' as const, text, isThinking: false },
    status: 'active' as const,
    timestamp: Date.now(),
  });
  const assistantItem = (id: string, text: string, isThinking = false) => ({
    id,
    type: 'assistant-message' as const,
    content: { $type: MessageType.Text, role: 'assistant' as const, text, isThinking },
  });
  const mountList = (displayItems: any[], isLoading = false) =>
    mount(MessageList, {
      props: { displayItems, isLoading },
      global: { stubs: { MetadataPill: true, NotificationPill: true } },
    });

  it('puts a copy button carrying the raw markdown in every finished assistant bubble, history and active', () => {
    const wrapper = mountList([
      userItem('u-1'),
      assistantItem('a-1', '# old **answer**'),
      userItem('u-2'),
      assistantItem('a-2', '- new [x](docs/a.md)'),
    ]);

    const bubbles = wrapper.findAll('[data-testid="assistant-text"]');
    expect(bubbles).toHaveLength(2);
    const texts = wrapper.findAllComponents(CopyMessageButton).map((c) => c.props('text'));
    expect(texts).toEqual(['# old **answer**', '- new [x](docs/a.md)']);
    for (const bubble of bubbles) {
      // A sibling of the bubble, never inside it: the browser E2E suite reads `assistant-text` by
      // innerText, and a "Copy" label in there would change every answer's text.
      expect(bubble.find('[data-testid="copy-message-button"]').exists()).toBe(false);
      expect(bubble.text()).not.toContain('Copy');
      const row = bubble.element.parentElement!;
      expect(row.querySelector(':scope > [data-testid="copy-message-button"]')).not.toBeNull();
    }
  });

  it('hides the copy button on the bubble that is still streaming', () => {
    const wrapper = mountList([userItem('u-1'), assistantItem('a-1', 'growing')], true);
    expect(wrapper.find('[data-testid="copy-message-button"]').exists()).toBe(false);
  });

  it('never puts a copy button on a user message', () => {
    const wrapper = mountList([userItem('u-1', 'question')]);
    expect(wrapper.find('[data-testid="copy-message-button"]').exists()).toBe(false);
  });

  it('turns on workspace links for assistant bubbles only', () => {
    const wrapper = mountList([userItem('u-1'), assistantItem('a-1', 'x')]);
    const byRole = Object.fromEntries(
      wrapper
        .findAllComponents(TextMessage)
        .map((c) => [c.props('message').role, c.props('workspaceLinks')])
    );
    expect(byRole).toEqual({ user: false, assistant: true });
  });

  it('gives a thinking bubble neither copy nor workspace links, in history or active, while answers keep both', () => {
    const wrapper = mountList([
      userItem('u-1'),
      assistantItem('t-1', 'old reasoning [x](docs/a.md)', true),
      assistantItem('a-1', 'old answer'),
      userItem('u-2'),
      assistantItem('t-2', 'new reasoning', true),
      assistantItem('a-2', 'new answer'),
    ]);

    expect(wrapper.findAllComponents(CopyMessageButton).map((c) => c.props('text'))).toEqual([
      'old answer',
      'new answer',
    ]);
    const linksByText = Object.fromEntries(
      wrapper
        .findAllComponents(TextMessage)
        .filter((c) => c.props('message').role === 'assistant')
        .map((c) => [c.props('message').text, c.props('workspaceLinks')])
    );
    expect(linksByText).toEqual({
      'old reasoning [x](docs/a.md)': false,
      'old answer': true,
      'new reasoning': false,
      'new answer': true,
    });
  });
});
