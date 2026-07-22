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
        >
          <button
            class="subagent-row"
            data-testid="subagent-focus-button"
            @click="emit('select', child.agentId)"
          >
            <div class="subagent-name">{{ child.name || child.template }}</div>
            <div class="subagent-task">{{ truncate(child.task, 60) }}</div>
            <div class="subagent-status">{{ child.status }}</div>
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
</style>
