import { describe, it, expect, vi } from 'vitest';
import { mount } from '@vue/test-utils';
import NotificationPill from '@/components/NotificationPill.vue';
import { type NotificationDisplayData } from '@/types';
import { GO_TO_AGENT_TAB } from '@/composables/useConversationTabs';
import { GET_AGENT_COLOR } from '@/utils/agentColors';
import { COMPACTION_NOTIFY_KIND, GET_CHECKPOINT_STATE } from '@/composables/messageDisplay';

describe('NotificationPill.vue', () => {
  it('renders a sub-agent completion with its message first and source metadata in details', async () => {
    const notification: NotificationDisplayData = {
      notifyKind: 'subagent-completion',
      sourceToolName: 'Spawn',
      sourceToolCallId: 'call_7',
      label: 'build-fixer',
      detail: 'all green',
    };
    const wrapper = mount(NotificationPill, { props: { notification } });

    const pill = wrapper.find('[data-testid="notification-pill"]');
    expect(pill.exists()).toBe(true);
    expect(pill.attributes('data-notify-kind')).toBe('subagent-completion');
    expect(wrapper.find('[data-testid="notification-label"]').text()).toContain('build-fixer');
    expect(wrapper.find('[data-testid="notification-source"]').exists()).toBe(false);
    await wrapper.get('button.notification-header').trigger('click');
    expect(wrapper.get('[data-testid="notification-source"]').text()).toBe('Spawn');
    expect(wrapper.get('.notification-detail').text()).toBe('all green');
    // It is NOT rendered as a user/assistant chat bubble.
    expect(wrapper.find('.markdown-content').exists()).toBe(false);
  });

  it('renders a legacy context-discovery notification with the file path and truncated badge', () => {
    const notification: NotificationDisplayData = {
      notifyKind: 'context-discovery',
      contextPath: 'AGENTS.md',
      contextTruncated: true,
      text: '<context-discovery path="AGENTS.md">…</context-discovery>',
    };
    const wrapper = mount(NotificationPill, { props: { notification } });

    const pill = wrapper.find('[data-testid="notification-pill"]');
    expect(pill.attributes('data-notify-kind')).toBe('context-discovery');
    expect(wrapper.find('[data-testid="notification-label"]').text()).toContain('AGENTS.md');
    expect(wrapper.find('[data-testid="notification-truncated"]').exists()).toBe(true);
  });

  // #244: an agent-to-agent message reuses this pill rather than adding another DisplayItem kind.
  it('renders an agent-to-agent message with a per-type heading and the sender name', () => {
    const notification: NotificationDisplayData = {
      notifyKind: 'agent-message',
      label: 'reviewer',
      sourceToolCallId: 'agent-2',
      detail: 'Which repo should I review first?',
      agentMessageType: 'Question',
    };
    const wrapper = mount(NotificationPill, { props: { notification } });

    const pill = wrapper.find('[data-testid="notification-pill"]');
    expect(pill.attributes('data-notify-kind')).toBe('agent-message');
    expect(pill.text()).toContain('Agent asked');
    expect(wrapper.find('[data-testid="notification-label"]').text()).toContain('reviewer');
    expect(wrapper.find('[data-testid="notification-source"]').exists()).toBe(false);
    expect(wrapper.find('.markdown-content').exists()).toBe(false);
  });

  it('names each agent message type distinctly', () => {
    const headings = (['Question', 'DelegateTask', 'TaskUpdate', 'Steer', 'Response'] as const).map(
      (agentMessageType) =>
        mount(NotificationPill, {
          props: { notification: { notifyKind: 'agent-message', agentMessageType } },
        })
          .find('[data-testid="notification-pill"]')
          .text()
    );

    expect(new Set(headings).size, 'each type reads differently').toBe(headings.length);
  });

  it('falls back to the raw type when a future agent message type arrives', () => {
    const wrapper = mount(NotificationPill, {
      props: {
        notification: {
          notifyKind: 'agent-message',
          agentMessageType: 'Escalate' as never,
        },
      },
    });

    expect(wrapper.find('[data-testid="notification-pill"]').text()).toContain('Escalate');
  });

  it('tints an agent message with the sender agent colour', () => {
    const wrapper = mount(NotificationPill, {
      props: {
        notification: {
          notifyKind: 'agent-message',
          label: 'reviewer',
          sourceToolCallId: 'agent-2',
          agentMessageType: 'Response',
        },
      },
      global: { provide: { [GET_AGENT_COLOR]: (id: string | null) => (id ? '#ff0000' : null) } },
    });

    // The colour is what matters, not the notation: jsdom re-serializes a hex colour in the
    // style attribute as `rgb(255, 0, 0)` (happy-dom kept the hex verbatim).
    const style = wrapper.find('[data-testid="notification-pill"]').attributes('style') ?? '';
    expect(style).toMatch(/#ff0000|rgb\(255,\s*0,\s*0\)/);
  });

  // #246 (fixed): a descendant (sub-agent) blocked on a browser-hosted client tool (e.g.
  // AskUserQuestion) surfaces through the SAME NotifyMessage/NotificationPill pipeline as
  // sub-agent-completion, but tagged with its OWN distinct notify_kind: 'descendant-question'.
  // This is deliberately NOT the same kind as the generic 'client-notification' (NotifyClient's
  // ad-hoc, non-blocking note) — those two have different source_tool_call_id semantics
  // (agentId/tab-id for descendant-question vs. the NotifyClient tool call's own id for
  // client-notification) and must not be conflated. No second notification channel — same pill,
  // different kind.
  it('renders a descendant-question with an outline icon and label', () => {
    const notification: NotificationDisplayData = {
      notifyKind: 'descendant-question',
      sourceToolName: 'AskUserQuestion',
      sourceToolCallId: 'agent-42',
      label: 'build-fixer needs input',
    };
    const wrapper = mount(NotificationPill, { props: { notification } });

    const pill = wrapper.find('[data-testid="notification-pill"]');
    expect(pill.attributes('data-notify-kind')).toBe('descendant-question');
    expect(wrapper.find('.notification-kind').text()).toBe('Question pending');
    expect(wrapper.get('svg.notification-icon').attributes('aria-hidden')).toBe('true');
  });

  it('navigates to the reporting descendant\'s tab when a descendant-question pill is clicked', async () => {
    const goToAgentTab = vi.fn();
    const notification: NotificationDisplayData = {
      notifyKind: 'descendant-question',
      sourceToolCallId: 'agent-42',
      label: 'build-fixer needs input',
    };
    const wrapper = mount(NotificationPill, {
      props: { notification },
      global: { provide: { [GO_TO_AGENT_TAB]: goToAgentTab } },
    });

    const header = wrapper.get('button.notification-header');
    expect(header.attributes('aria-expanded')).toBeUndefined();
    expect(header.attributes('aria-controls')).toBeUndefined();
    await header.trigger('click');

    expect(goToAgentTab).toHaveBeenCalledWith('agent-42');
  });

  it('does not attempt navigation for a descendant-question with no sourceToolCallId', async () => {
    const goToAgentTab = vi.fn();
    const notification: NotificationDisplayData = {
      notifyKind: 'descendant-question',
      label: 'needs input',
    };
    const wrapper = mount(NotificationPill, {
      props: { notification },
      global: { provide: { [GO_TO_AGENT_TAB]: goToAgentTab } },
    });

    const header = wrapper.get('.notification-header');
    expect(header.element.tagName).toBe('DIV');
    await header.trigger('click');

    expect(goToAgentTab).not.toHaveBeenCalled();
  });

  // #246 spec-defect fix: NotifyClient's own ad-hoc, non-blocking notification is ALWAYS tagged
  // notify_kind: 'client-notification', with source_tool_call_id set to the NotifyClient tool
  // call's OWN id (NotifyClientToolProvider.HandleAsync) — never an agent/tab id, whether the
  // call came from the primary loop or from inside a sub-agent's own loop. It must never be
  // treated as navigable just because sourceToolCallId happens to be present; it must remain
  // expandable like every other non-navigable notification kind.
  it('renders a root/ad-hoc client-notification as a generic, non-navigable notification', () => {
    const notification: NotificationDisplayData = {
      notifyKind: 'client-notification',
      sourceToolName: 'NotifyClient',
      sourceToolCallId: 'call_99',
      label: 'Heads up',
      detail: 'Cleanup finished',
    };
    const wrapper = mount(NotificationPill, { props: { notification } });

    const pill = wrapper.find('[data-testid="notification-pill"]');
    expect(pill.attributes('data-notify-kind')).toBe('client-notification');
    expect(wrapper.find('.notification-kind').text()).toBe('Notification');
    expect(wrapper.get('[data-testid="notification-label"]').text()).toBe('Heads up');
    expect(wrapper.find('[data-testid="notification-source"]').exists()).toBe(false);
  });

  it('does not navigate for a generic client-notification even when sourceToolCallId is present, and stays expandable', async () => {
    const goToAgentTab = vi.fn();
    const notification: NotificationDisplayData = {
      notifyKind: 'client-notification',
      sourceToolName: 'NotifyClient',
      sourceToolCallId: 'call_99',
      detail: 'Cleanup finished',
    };
    const wrapper = mount(NotificationPill, {
      props: { notification },
      global: { provide: { [GO_TO_AGENT_TAB]: goToAgentTab } },
    });

    const header = wrapper.get('button.notification-header');
    const detailId = header.attributes('aria-controls');
    expect(header.attributes('aria-expanded')).toBe('false');
    expect(detailId).toBeTruthy();
    await header.trigger('click');

    expect(goToAgentTab).not.toHaveBeenCalled();
    expect(header.attributes('aria-expanded')).toBe('true');
    expect(wrapper.get('[data-testid="notification-body"]').attributes('id')).toBe(detailId);
    expect(wrapper.get('[data-testid="notification-source"]').text()).toBe('NotifyClient');
    expect(wrapper.get('.notification-detail').text()).toBe('Cleanup finished');
  });

  it('leaves other notification kinds unaffected by navigation (still just expands/collapses)', async () => {
    const goToAgentTab = vi.fn();
    const notification: NotificationDisplayData = {
      notifyKind: 'subagent-completion',
      sourceToolCallId: 'agent-7',
      label: 'build-fixer',
      detail: 'all green',
    };
    const wrapper = mount(NotificationPill, {
      props: { notification },
      global: { provide: { [GO_TO_AGENT_TAB]: goToAgentTab } },
    });

    await wrapper.find('.notification-header').trigger('click');

    expect(goToAgentTab).not.toHaveBeenCalled();
    expect(wrapper.find('[data-testid="notification-body"]').exists()).toBe(true);
  });

  // #721 / spec 679 §7.2-7.3: a compaction checkpoint renders as a full-width divider through this
  // same pill, expands to the manifest, and carries a badge when the context report says it rolled back.
  it('renders a compaction checkpoint as a full-width divider that expands to the manifest', async () => {
    const notification: NotificationDisplayData = {
      notifyKind: COMPACTION_NOTIFY_KIND,
      checkpointId: 'cp-x-1',
      label: '12 rows · ~16,000 tokens saved',
      detail: '## What happened\nTurn one gathered the data.',
    };
    const wrapper = mount(NotificationPill, { props: { notification } });

    const pill = wrapper.get('[data-testid="notification-pill"]');
    expect(pill.attributes('data-notify-kind')).toBe('compaction');
    expect(pill.attributes('data-checkpoint-id')).toBe('cp-x-1');
    expect(pill.classes()).toContain('compaction-divider');
    expect(wrapper.find('.notification-kind').text()).toBe('Context compacted');
    expect(wrapper.find('[data-testid="notification-label"]').text()).toBe('12 rows · ~16,000 tokens saved');
    expect(wrapper.find('[data-testid="compaction-badge"]').exists()).toBe(false);

    await wrapper.find('.notification-header').trigger('click');
    expect(wrapper.get('[data-testid="notification-body"]').text()).toContain('Turn one gathered the data.');
  });

  it('badges a compaction divider whose checkpoint the context report says rolled back', () => {
    const wrapper = mount(NotificationPill, {
      props: { notification: { notifyKind: COMPACTION_NOTIFY_KIND, checkpointId: 'cp-x-1' } },
      global: {
        provide: { [GET_CHECKPOINT_STATE]: (id: string) => (id === 'cp-x-1' ? 'RolledBack' : null) },
      },
    });

    expect(wrapper.get('[data-testid="compaction-badge"]').text()).toBe('rolled back');
  });
});
