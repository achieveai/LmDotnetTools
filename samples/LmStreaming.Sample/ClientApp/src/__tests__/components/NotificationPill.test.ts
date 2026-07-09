import { describe, it, expect } from 'vitest';
import { mount } from '@vue/test-utils';
import NotificationPill from '@/components/NotificationPill.vue';
import { type NotifyMessage, type NotificationDisplayData, MessageType } from '@/types';

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

  it('accepts a raw NotifyMessage and normalizes its snake_case fields', () => {
    const message: NotifyMessage = {
      $type: MessageType.Notify,
      role: 'user',
      text: '<notification kind="subagent-completion" label="agent-x">body</notification>',
      notify_kind: 'subagent-completion',
      source_tool_name: 'Spawn',
      label: 'agent-x',
      detail: 'body',
    };
    const wrapper = mount(NotificationPill, { props: { notification: message } });

    const pill = wrapper.find('[data-testid="notification-pill"]');
    expect(pill.attributes('data-notify-kind')).toBe('subagent-completion');
    expect(wrapper.find('[data-testid="notification-label"]').text()).toContain('agent-x');
    expect(wrapper.find('[data-testid="notification-source"]').text()).toContain('Spawn');
  });
});
