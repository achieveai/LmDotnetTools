import { computed, reactive, ref, watch, type ComputedRef, type Ref } from 'vue';
import type { SubAgentSummary, SubAgentStatus } from '@/api/subAgentsApi';
import { hueForIndex, type AgentColorLookup } from '@/utils/agentColors';

/** The id of the always-present top-level tab. */
export const MAIN_TAB_ID = 'main';

/** One entry in the center-pane tab strip: the `main` conversation, a sub-agent, or a workflow run. */
export interface ConversationTab {
  /** `'main'` for the top-level agent, else the sub-agent's / workflow's `agentId`. */
  id: string;
  /** Display label: `'main'`, or the child's `name || template`. */
  label: string;
  kind: 'main' | 'subagent' | 'workflow';
  /** Assigned hue for a sub-agent/workflow, or null for the neutral `main` tab. */
  color: string | null;
  status?: SubAgentStatus;
}

/**
 * The slice of {@link useSubAgentPanel}'s surface this router needs. Passed IN (never re-instantiated
 * here) so `useConversationTabs` stays a pure selector/router over the already-created composable —
 * re-instantiating would spin up a second poller/socket.
 */
export interface ConversationTabsDeps {
  children: Ref<SubAgentSummary[]>;
  focusedAgentId: Ref<string | null>;
  focusChild: (agentId: string) => Promise<void> | void;
  unfocusChild: () => Promise<void> | void;
  /** Resolves the active parent conversation's thread id (to reset the active tab on switch). */
  getParentThreadId: () => string | null;
}

/**
 * Owns "which conversation is shown in the center pane, and how selecting/coloring it works." It holds
 * ONLY the tab-selection state (`activeTabId`) and the discovery-order color map; message state,
 * sockets, polling and send side-effects stay in `useChat` / `useSubAgentPanel` / `ChatLayout`.
 *
 * Connection model = connect-on-activate: selecting a sub-agent tab focuses that child (one live
 * stream at a time), selecting `main` unfocuses. `focusChild` already tears down any prior focus, so
 * A→B switching is correct without extra teardown here.
 */
export function useConversationTabs(deps: ConversationTabsDeps) {
  const { children, focusedAgentId, focusChild, unfocusChild, getParentThreadId } = deps;

  const activeTabId = ref<string>(MAIN_TAB_ID);

  // agentId → hue, assigned once per agent in the order it is first DISCOVERED and kept stable for the
  // session (so an agent's color never shifts as the polled list mutates). Assigned in a watch (a
  // side-effect), never inside the `tabs` computed getter. REACTIVE so a color assigned on a later poll
  // reactively reaches BOTH the tabs and the inline call pills (ToolPill / NotificationPill inject
  // getAgentColor) whose render may precede the child's first appearance in the list.
  const colorByAgent = reactive<Record<string, string>>({});
  let nextColorIndex = 0;

  function assignColors(list: readonly SubAgentSummary[]): void {
    for (const child of list) {
      if (!(child.agentId in colorByAgent)) {
        colorByAgent[child.agentId] = hueForIndex(nextColorIndex);
        nextColorIndex += 1;
      }
    }
  }

  const getAgentColor: AgentColorLookup = (agentId) => (agentId ? colorByAgent[agentId] ?? null : null);

  // Assign colors as children are discovered. `immediate` so any children already present at setup are
  // colored before the first render reads `tabs`.
  watch(children, (list) => assignColors(list), { immediate: true });

  // Keep the active tab valid: snap back to `main` when the parent conversation changes (before the
  // child list clears, avoiding a stale-agent flash), or when the active sub-agent leaves the list.
  watch(getParentThreadId, () => {
    if (activeTabId.value !== MAIN_TAB_ID) {
      activeTabId.value = MAIN_TAB_ID;
    }
  });
  watch(children, (list) => {
    if (activeTabId.value !== MAIN_TAB_ID && !list.some((c) => c.agentId === activeTabId.value)) {
      activeTabId.value = MAIN_TAB_ID;
      void unfocusChild();
    }
  });

  const tabs: ComputedRef<ConversationTab[]> = computed(() => {
    const list: ConversationTab[] = [
      { id: MAIN_TAB_ID, label: 'main', kind: 'main', color: null },
    ];
    for (const child of children.value) {
      // Workflow runs arrive in the SAME children list as sub-agents (kind: 'workflow'); surface them
      // as tabs identically, only tagging the kind so the strip can badge them distinctly. A missing
      // kind (older server) is treated as a plain sub-agent.
      list.push({
        id: child.agentId,
        label: child.name || child.template,
        kind: child.kind === 'workflow' ? 'workflow' : 'subagent',
        color: getAgentColor(child.agentId),
        status: child.status,
      });
    }
    return list;
  });

  /**
   * Switch the center pane to a tab. `main` unfocuses the active child; a sub-agent tab focuses it
   * (connect-on-activate). Sets `activeTabId` first so the view swaps immediately while `focusChild`'s
   * async history/stream load resolves under the (empty → populated) transcript.
   */
  async function selectTab(id: string): Promise<void> {
    if (id === activeTabId.value) return;
    if (id === MAIN_TAB_ID) {
      activeTabId.value = MAIN_TAB_ID;
      await unfocusChild();
      return;
    }
    activeTabId.value = id;
    await focusChild(id);
  }

  return { activeTabId, tabs, selectTab, getAgentColor, focusedAgentId };
}
