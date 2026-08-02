<script setup lang="ts">
import { ref } from 'vue';
import type { SubAgentSummary } from '@/api/subAgentsApi';

/**
 * Compact right-side LAUNCHER for a conversation's sub-agents. Stateless/presentational: it renders the
 * shared `children` list (owned by ChatLayout's `useSubAgentPanel`) and emits `select(agentId)` to
 * activate that sub-agent's center-pane tab. The transcript + reply input now live in the center tab
 * (`SubAgentTranscript`), not here.
 */
const props = defineProps<{
  children: SubAgentSummary[];
  /** The active center tab (`'main'` or an agentId) — highlights the matching row. */
  activeTabId: string;
}>();

const emit = defineEmits<{ select: [agentId: string] }>();

const expanded = ref(false);
function toggle(): void {
  expanded.value = !expanded.value;
}

function truncate(text: string, max: number): string {
  if (!text) return '';
  return text.length <= max ? text : text.slice(0, max) + '...';
}

/**
 * Hierarchy affordances (#244). All three fields are OPTIONAL: a server with collaboration off, or a
 * row persisted by a pre-#244 build, omits them, and the row must then render exactly as it always
 * did — hence `== null` checks rather than falsy ones (`structuralDepth: 0` is the root's real depth).
 */

/** Indents a row by its structural depth so the tree is visible without a second layout. */
function indentStyle(child: SubAgentSummary): Record<string, string> {
  const depth = child.structuralDepth;
  return depth == null || depth <= 0 ? {} : { paddingLeft: `${14 + depth * 12}px` };
}

/** Long-form explanation of BOTH depths, shown on hover of the compact badge. */
function depthTitle(child: SubAgentSummary): string {
  const parts = [`Structural depth ${child.structuralDepth}`];
  if (child.delegationDepth != null) {
    parts.push(`delegation depth ${child.delegationDepth}`);
  }
  return parts.join(' · ');
}

/**
 * `data-*` attributes must be strings: Vue DROPS an attribute bound to `false`, which would make an
 * unreadable row indistinguishable from a row that never said. Null/undefined stay dropped on purpose.
 */
function attr(value: number | boolean | null | undefined): string | undefined {
  return value == null ? undefined : String(value);
}
</script>

<template>
  <aside class="subagent-panel-container">
    <button
      class="subagent-toggle"
      data-testid="subagent-panel-toggle"
      :title="expanded ? 'Collapse sub-agents' : 'Expand sub-agents'"
      @click="toggle"
    >
      Sub-agents ({{ props.children.length }})
      <span class="subagent-toggle-caret">{{ expanded ? '▸' : '◂' }}</span>
    </button>

    <div v-if="expanded" class="subagent-panel" data-testid="subagent-panel">
      <ul class="subagent-list" data-testid="subagent-list">
        <li v-if="props.children.length === 0" class="subagent-empty">No sub-agents yet.</li>
        <li
          v-for="child in props.children"
          :key="child.agentId"
          :class="['subagent-item', { focused: child.agentId === props.activeTabId }]"
          data-testid="subagent-item"
          :data-agent-id="child.agentId"
          :data-structural-depth="attr(child.structuralDepth)"
          :data-delegation-depth="attr(child.delegationDepth)"
          :data-transcript-readable="attr(child.isReadable)"
        >
          <button
            class="subagent-row"
            data-testid="subagent-focus-button"
            :style="indentStyle(child)"
            @click="emit('select', child.agentId)"
          >
            <div class="subagent-name">
              {{ child.name || child.template }}
              <span
                v-if="child.isReadable === false"
                class="subagent-locked"
                data-testid="subagent-transcript-locked"
                title="You cannot read this agent's transcript."
                aria-label="Transcript not readable"
                >🔒</span
              >
            </div>
            <div class="subagent-task">{{ truncate(child.task, 60) }}</div>
            <div class="subagent-status">
              {{ child.status }}
              <span
                v-if="child.structuralDepth != null"
                class="subagent-depth"
                data-testid="subagent-depth"
                :title="depthTitle(child)"
                >· L{{ child.structuralDepth
                }}<template v-if="child.delegationDepth != null">/D{{ child.delegationDepth }}</template></span
              >
            </div>
          </button>
        </li>
      </ul>
    </div>
  </aside>
</template>

<style scoped>
.subagent-panel-container {
  display: flex;
  flex-direction: column;
  border-left: 1px solid #e0e0e0;
  background: #f8f9fa;
  min-width: 48px;
}

.subagent-toggle {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
  padding: 12px 14px;
  background: transparent;
  border: none;
  border-bottom: 1px solid #e0e0e0;
  cursor: pointer;
  font-size: 14px;
  font-weight: 500;
  color: #212529;
  white-space: nowrap;
}

.subagent-toggle:hover {
  background: #e9ecef;
}

.subagent-toggle-caret {
  color: #666;
  font-size: 12px;
}

.subagent-panel {
  width: 300px;
  min-width: 300px;
  flex: 1;
  display: flex;
  flex-direction: column;
  overflow: hidden;
}

.subagent-list {
  list-style: none;
  padding: 0;
  margin: 0;
  overflow-y: auto;
  flex: 1;
}

.subagent-empty {
  padding: 16px;
  text-align: center;
  color: #666;
  font-size: 13px;
}

.subagent-item {
  border-bottom: 1px solid #e0e0e0;
}

.subagent-item.focused {
  background: #d4e5f7;
  border-left: 3px solid #007bff;
}

.subagent-row {
  display: block;
  width: 100%;
  text-align: left;
  padding: 10px 14px;
  background: transparent;
  border: none;
  cursor: pointer;
}

.subagent-item.focused .subagent-row {
  padding-left: 11px;
}

.subagent-row:hover {
  background: #eef2f7;
}

.subagent-name {
  font-weight: 500;
  font-size: 13px;
  color: #212529;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}

.subagent-task {
  font-size: 12px;
  color: #6c757d;
  margin: 2px 0;
}

.subagent-status {
  font-size: 11px;
  color: #adb5bd;
  text-transform: capitalize;
}

/* Compact hierarchy badge: L = structural depth, D = delegation depth (#244). */
.subagent-depth {
  font-variant-numeric: tabular-nums;
  text-transform: none;
}

.subagent-locked {
  font-size: 11px;
  margin-left: 4px;
}
</style>
