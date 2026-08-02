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

/**
 * #244 hierarchy metadata. The API has published `structuralDepth` / `delegationDepth` / `isReadable`
 * since the collaboration work landed, but nothing RENDERED them — a parsed field the user never sees
 * is indistinguishable from a missing one. These assert the row shows both depths and says when a
 * transcript is closed to the reader, while a pre-#244 row (all three absent) renders exactly as before.
 */
describe('SubAgentListPanel — hierarchy metadata (#244)', () => {
  async function expand(children: SubAgentSummary[], activeTabId = 'main') {
    const wrapper = mountPanel(children, activeTabId);
    await wrapper.get('[data-testid="subagent-panel-toggle"]').trigger('click');
    return wrapper;
  }

  it('publishes both depths and the transcript-readable flag as row attributes', async () => {
    const wrapper = await expand([
      summary('a1', { structuralDepth: 2, delegationDepth: 1, isReadable: true }),
    ]);

    const item = wrapper.get('[data-testid="subagent-item"]');
    expect(item.attributes('data-structural-depth')).toBe('2');
    expect(item.attributes('data-delegation-depth')).toBe('1');
    expect(item.attributes('data-transcript-readable')).toBe('true');
  });

  it('renders a depth badge carrying both depths', async () => {
    const wrapper = await expand([
      summary('a1', { structuralDepth: 2, delegationDepth: 1 }),
    ]);

    const badge = wrapper.get('[data-testid="subagent-depth"]');
    expect(badge.text()).toContain('L2');
    expect(badge.text()).toContain('D1');
    expect(badge.attributes('title')).toBe('Structural depth 2 · delegation depth 1');
  });

  it('renders depth 0 (the row directly under the root), not treating it as absent', async () => {
    const wrapper = await expand([summary('a1', { structuralDepth: 0, delegationDepth: 0 })]);

    const item = wrapper.get('[data-testid="subagent-item"]');
    expect(item.attributes('data-structural-depth')).toBe('0');
    expect(wrapper.get('[data-testid="subagent-depth"]').text()).toContain('L0');
  });

  it('indents a row by its structural depth', async () => {
    const wrapper = await expand([
      summary('a1', { structuralDepth: 0 }),
      summary('a2', { structuralDepth: 3 }),
    ]);

    const rows = wrapper.findAll('[data-testid="subagent-focus-button"]');
    expect(rows[0].attributes('style')).toBeUndefined();
    expect(rows[1].attributes('style')).toContain('padding-left: 50px');
  });

  it('marks a row the reader may not read, and leaves a readable one unmarked', async () => {
    const wrapper = await expand([
      summary('a1', { isReadable: false }),
      summary('a2', { isReadable: true }),
    ]);

    const items = wrapper.findAll('[data-testid="subagent-item"]');
    expect(items[0].find('[data-testid="subagent-transcript-locked"]').exists()).toBe(true);
    expect(items[0].attributes('data-transcript-readable')).toBe('false');
    expect(items[1].find('[data-testid="subagent-transcript-locked"]').exists()).toBe(false);
  });

  it('leaves a pre-#244 row exactly as it was: no depth badge, no lock, no attributes', async () => {
    const wrapper = await expand([summary('a1')]);

    const item = wrapper.get('[data-testid="subagent-item"]');
    expect(item.attributes('data-structural-depth')).toBeUndefined();
    expect(item.attributes('data-delegation-depth')).toBeUndefined();
    expect(item.attributes('data-transcript-readable')).toBeUndefined();
    expect(wrapper.find('[data-testid="subagent-depth"]').exists()).toBe(false);
    expect(wrapper.find('[data-testid="subagent-transcript-locked"]').exists()).toBe(false);
    expect(item.text()).toContain('Agent a1');
  });
});
