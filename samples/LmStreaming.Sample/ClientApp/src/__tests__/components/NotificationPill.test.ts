import { describe, it, expect, vi } from 'vitest';
import { mount } from '@vue/test-utils';
import NotificationPill from '@/components/NotificationPill.vue';
import { type NotificationDisplayData } from '@/types';
import { GET_AGENT_COLOR } from '@/utils/agentColors';
import { GO_TO_AGENT_TAB } from '@/composables/useConversationTabs';

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

    expect(wrapper.find('[data-testid="notification-pill"]').attributes('style')).toContain('#ff0000');
  });

  it('renders a client-notification (pending question) with its own icon and label', () => {
    const notification: NotificationDisplayData = {
      notifyKind: 'client-notification',
      sourceToolName: 'AskUserQuestion',
      sourceToolCallId: 'agent-42',
      label: 'build-fixer needs input',
    };
    const wrapper = mount(NotificationPill, { props: { notification } });

    const pill = wrapper.find('[data-testid="notification-pill"]');
    expect(pill.attributes('data-notify-kind')).toBe('client-notification');
    expect(wrapper.find('.notification-kind').text()).toBe('Question pending');
    expect(wrapper.find('.notification-icon').text()).toBe('❓');
  });

  it("navigates to the reporting descendant's tab when a client-notification pill is clicked", async () => {
    const goToAgentTab = vi.fn();
    const notification: NotificationDisplayData = {
      notifyKind: 'client-notification',
      sourceToolCallId: 'agent-42',
      label: 'build-fixer needs input',
    };
    const wrapper = mount(NotificationPill, {
      props: { notification },
      global: { provide: { [GO_TO_AGENT_TAB]: goToAgentTab } },
    });

    await wrapper.find('.notification-header').trigger('click');
    expect(goToAgentTab).toHaveBeenCalledWith('agent-42');
  });

  it('does not attempt navigation for a client-notification with no sourceToolCallId', async () => {
    const goToAgentTab = vi.fn();
    const notification: NotificationDisplayData = {
      notifyKind: 'client-notification',
      label: 'needs input',
    };
    const wrapper = mount(NotificationPill, {
      props: { notification },
      global: { provide: { [GO_TO_AGENT_TAB]: goToAgentTab } },
    });

    await wrapper.find('.notification-header').trigger('click');
    expect(goToAgentTab).not.toHaveBeenCalled();
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
});
