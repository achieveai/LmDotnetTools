import { describe, it, expect } from 'vitest';
import { mount } from '@vue/test-utils';
import SubAgentListPanel from '@/components/SubAgentListPanel.vue';
import type { SubAgentSummary } from '@/api/subAgentsApi';

// The panel is now a stateless LAUNCHER: it takes the shared `children` + `activeTabId` as props and
// emits `select(agentId)`. It no longer owns a composable, transcript, input, or error banner (those
// moved to ChatLayout / SubAgentTranscript).

function summary(agentId: string, overrides: Partial<SubAgentSummary> = {}): SubAgentSummary {
  return {
    agentId,
    name: `Agent ${agentId}`,
    template: 'research',
    task: 'do a lot of important work here',
    status: 'running',
    threadId: `subagent-${agentId}`,
    lastActivityUtc: null,
    ...overrides,
  };
}

function mountPanel(children: SubAgentSummary[] = [], activeTabId = 'main') {
  return mount(SubAgentListPanel, { props: { children, activeTabId } });
}

describe('SubAgentListPanel (launcher)', () => {
  it('is collapsed by default: shows the toggle with the child count, panel hidden', () => {
    const wrapper = mountPanel([summary('a1'), summary('a2')]);
    const toggle = wrapper.get('[data-testid="subagent-panel-toggle"]');
    expect(toggle.text()).toContain('Sub-agents (2)');
    expect(wrapper.find('[data-testid="subagent-panel"]').exists()).toBe(false);
  });

  it('expands to show the list of children on toggle', async () => {
    const wrapper = mountPanel([summary('a1'), summary('a2', { name: null, template: 'planner' })]);

    await wrapper.get('[data-testid="subagent-panel-toggle"]').trigger('click');

    expect(wrapper.find('[data-testid="subagent-panel"]').exists()).toBe(true);
    const items = wrapper.findAll('[data-testid="subagent-item"]');
    expect(items).toHaveLength(2);
    expect(items[0].attributes('data-agent-id')).toBe('a1');
    expect(items[0].text()).toContain('Agent a1');
    expect(items[0].text()).toContain('running');
    // name falls back to the template when name is null.
    expect(items[1].text()).toContain('planner');
  });

  it('shows an empty state when there are no children', async () => {
    const wrapper = mountPanel([]);
    await wrapper.get('[data-testid="subagent-panel-toggle"]').trigger('click');
    expect(wrapper.get('[data-testid="subagent-list"]').text()).toContain('No sub-agents yet.');
  });

  it('clicking a row emits select with the agent id', async () => {
    const wrapper = mountPanel([summary('a1')]);
    await wrapper.get('[data-testid="subagent-panel-toggle"]').trigger('click');

    await wrapper.get('[data-testid="subagent-focus-button"]').trigger('click');
    expect(wrapper.emitted('select')).toEqual([['a1']]);
  });

  it('highlights the row matching the active tab', async () => {
    const wrapper = mountPanel([summary('a1'), summary('a2')], 'a2');
    await wrapper.get('[data-testid="subagent-panel-toggle"]').trigger('click');

    const items = wrapper.findAll('[data-testid="subagent-item"]');
    expect(items[0].classes()).not.toContain('focused');
    expect(items[1].classes()).toContain('focused');
  });
});
