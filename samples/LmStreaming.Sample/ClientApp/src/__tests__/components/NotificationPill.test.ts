import { describe, it, expect } from 'vitest';
import { mount } from '@vue/test-utils';
import NotificationPill from '@/components/NotificationPill.vue';
import { type NotificationDisplayData } from '@/types';
import { GET_AGENT_COLOR } from '@/utils/agentColors';

describe('NotificationPill.vue', () => {
  it('renders a sub-agent completion notification with kind, source tool and label', () => {
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
    expect(wrapper.find('[data-testid="notification-source"]').text()).toContain('Spawn');
    expect(wrapper.find('[data-testid="notification-label"]').text()).toContain('build-fixer');
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

  // #244: an agent-to-agent message reuses this pill rather than adding a fifth DisplayItem kind.
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
    // No `sourceToolName` — an agent message is not the product of a tool call.
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
          // Deliberately not in the union: a server-side addition must not render blank.
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

    // The pill matches the sender's tab colour, so the reader can see WHO spoke at a glance.
    expect(wrapper.find('[data-testid="notification-pill"]').attributes('style')).toContain(
      '#ff0000'
    );
  });
});
