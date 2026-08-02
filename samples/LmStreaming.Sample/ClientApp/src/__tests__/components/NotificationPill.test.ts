import { describe, it, expect, vi } from 'vitest';
import { mount } from '@vue/test-utils';
import NotificationPill from '@/components/NotificationPill.vue';
import { type NotificationDisplayData } from '@/types';
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

  // #246 (fixed): a descendant (sub-agent) blocked on a browser-hosted client tool (e.g.
  // AskUserQuestion) surfaces through the SAME NotifyMessage/NotificationPill pipeline as
  // sub-agent-completion, but tagged with its OWN distinct notify_kind: 'descendant-question'.
  // This is deliberately NOT the same kind as the generic 'client-notification' (NotifyClient's
  // ad-hoc, non-blocking note) — those two have different source_tool_call_id semantics
  // (agentId/tab-id for descendant-question vs. the NotifyClient tool call's own id for
  // client-notification) and must not be conflated. No second notification channel — same pill,
  // different kind.
  it('renders a descendant-question (pending question) with its own icon and label', () => {
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
    expect(wrapper.find('.notification-icon').text()).toBe('❓');
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

    await wrapper.find('.notification-header').trigger('click');

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

    await wrapper.find('.notification-header').trigger('click');

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

    await wrapper.find('.notification-header').trigger('click');

    expect(goToAgentTab).not.toHaveBeenCalled();
    expect(wrapper.find('[data-testid="notification-body"]').exists()).toBe(true);
    expect(wrapper.find('[data-testid="notification-body"]').text()).toBe('Cleanup finished');
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
