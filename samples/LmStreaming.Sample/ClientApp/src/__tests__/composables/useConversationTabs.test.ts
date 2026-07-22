import { describe, it, expect, vi } from 'vitest';
import { ref, computed, effectScope, nextTick } from 'vue';
import { useConversationTabs, MAIN_TAB_ID } from '@/composables/useConversationTabs';
import { AGENT_HUES } from '@/utils/agentColors';
import type { SubAgentSummary } from '@/api/subAgentsApi';

function child(agentId: string, over: Partial<SubAgentSummary> = {}): SubAgentSummary {
  return {
    agentId,
    name: `Agent ${agentId}`,
    template: 'research',
    task: 'work',
    status: 'running',
    threadId: `subagent-${agentId}`,
    lastActivityUtc: null,
    ...over,
  };
}

/** Run the composable inside an effect scope so its watchers are created (and disposable). */
function harness(initialChildren: SubAgentSummary[] = [], parentThreadId: string | null = 'parent-1') {
  const children = ref<SubAgentSummary[]>(initialChildren);
  const focusedAgentId = ref<string | null>(null);
  const parent = ref<string | null>(parentThreadId);
  const focusChild = vi.fn(async (id: string) => {
    focusedAgentId.value = id;
  });
  const unfocusChild = vi.fn(async () => {
    focusedAgentId.value = null;
  });
  const scope = effectScope();
  const api = scope.run(() =>
    useConversationTabs({
      children,
      focusedAgentId,
      focusChild,
      unfocusChild,
      getParentThreadId: () => parent.value,
    })
  )!;
  return { api, children, focusedAgentId, parent, focusChild, unfocusChild, scope };
}

describe('useConversationTabs', () => {
  it('always shows a main tab first, then one tab per child with name||template labels', () => {
    const { api } = harness([child('a1'), child('a2', { name: null, template: 'planner' })]);
    const tabs = api.tabs.value;
    expect(tabs.map((t) => t.id)).toEqual([MAIN_TAB_ID, 'a1', 'a2']);
    expect(tabs[0]).toMatchObject({ kind: 'main', label: 'main', color: null });
    expect(tabs[1]).toMatchObject({ kind: 'subagent', label: 'Agent a1' });
    // name falls back to template when null
    expect(tabs[2].label).toBe('planner');
  });

  it('assigns colors in discovery order and keeps them stable as the list mutates', async () => {
    const { api, children } = harness([child('a1'), child('a2')]);
    expect(api.getAgentColor('a1')).toBe(AGENT_HUES[0]);
    expect(api.getAgentColor('a2')).toBe(AGENT_HUES[1]);

    // a1 drops out, a3 appears: a2 keeps its hue, a3 gets the next index (not a1's freed slot).
    children.value = [child('a2'), child('a3')];
    await nextTick();
    expect(api.getAgentColor('a2')).toBe(AGENT_HUES[1]);
    expect(api.getAgentColor('a3')).toBe(AGENT_HUES[2]);
    expect(api.getAgentColor('unknown')).toBeNull();
  });

  it('getAgentColor is reactive: a computed re-evaluates when the color is assigned on a later poll', async () => {
    // Models an inline pill: it renders BEFORE the child appears in the polled list, so its color
    // resolves to null first and must reactively pick up the hue once the child is discovered.
    const { api, children, scope } = harness([]);
    const pillColor = scope.run(() => computed(() => api.getAgentColor('late-agent')))!;
    expect(pillColor.value).toBeNull();

    children.value = [child('late-agent')];
    await nextTick();
    expect(pillColor.value).toBe(AGENT_HUES[0]);
  });

  it('selectTab(main) unfocuses; selectTab(agent) focuses that child', async () => {
    const { api, focusChild, unfocusChild } = harness([child('a1')]);
    await api.selectTab('a1');
    expect(focusChild).toHaveBeenCalledWith('a1');
    expect(api.activeTabId.value).toBe('a1');

    await api.selectTab(MAIN_TAB_ID);
    expect(unfocusChild).toHaveBeenCalledTimes(1);
    expect(api.activeTabId.value).toBe(MAIN_TAB_ID);
  });

  it('is a no-op when selecting the already-active tab', async () => {
    const { api, focusChild } = harness([child('a1')]);
    await api.selectTab(MAIN_TAB_ID);
    expect(focusChild).not.toHaveBeenCalled();
  });

  it('snaps back to main when the parent conversation changes', async () => {
    const { api, parent } = harness([child('a1')]);
    await api.selectTab('a1');
    expect(api.activeTabId.value).toBe('a1');

    parent.value = 'parent-2';
    await nextTick();
    expect(api.activeTabId.value).toBe(MAIN_TAB_ID);
  });

  it('snaps back to main and unfocuses when the active sub-agent leaves the list', async () => {
    const { api, children, unfocusChild } = harness([child('a1')]);
    await api.selectTab('a1');
    unfocusChild.mockClear();

    children.value = [child('a2')];
    await nextTick();
    expect(api.activeTabId.value).toBe(MAIN_TAB_ID);
    expect(unfocusChild).toHaveBeenCalled();
  });
});
