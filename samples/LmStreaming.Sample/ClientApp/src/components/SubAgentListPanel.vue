<script setup lang="ts">
import { ref, onMounted, provide } from 'vue';
import { useSubAgentPanel } from '@/composables/useSubAgentPanel';
import MessageList from './MessageList.vue';
import ChatInput from './ChatInput.vue';

const props = defineProps<{
  parentThreadId: string | null;
}>();

const {
  children,
  focusedAgentId,
  focusedDisplayItems,
  isFocusedStreaming,
  startPolling,
  focusChild,
  unfocusChild,
  sendToFocusedChild,
  getResultForToolCall,
} = useSubAgentPanel(() => props.parentThreadId);

// Nested tool pills inside the focused transcript must resolve against the CHILD's tool results, not
// the parent chat's. provide/inject is component-subtree scoped, so this overrides ChatLayout's
// provide('getResultForToolCall', …) ONLY for the MessageList rendered inside this panel.
provide('getResultForToolCall', getResultForToolCall);

const expanded = ref(false);

function toggle(): void {
  expanded.value = !expanded.value;
}

function handleSend(text: string): void {
  sendToFocusedChild(text);
}

function truncate(text: string, max: number): string {
  if (!text) return '';
  return text.length <= max ? text : text.slice(0, max) + '...';
}

onMounted(() => {
  startPolling();
});
// Teardown (stopPolling + unfocusChild) is handled by the composable's onScopeDispose when this
// component unmounts, so no explicit onBeforeUnmount hook is needed here.
</script>

<template>
  <aside class="subagent-panel-container">
    <button
      class="subagent-toggle"
      data-testid="subagent-panel-toggle"
      :title="expanded ? 'Collapse sub-agents' : 'Expand sub-agents'"
      @click="toggle"
    >
      Sub-agents ({{ children.length }})
      <span class="subagent-toggle-caret">{{ expanded ? '▸' : '◂' }}</span>
    </button>

    <div v-if="expanded" class="subagent-panel" data-testid="subagent-panel">
      <ul class="subagent-list" data-testid="subagent-list">
        <li v-if="children.length === 0" class="subagent-empty">
          No sub-agents yet.
        </li>
        <li
          v-for="child in children"
          :key="child.agentId"
          :class="['subagent-item', { focused: child.agentId === focusedAgentId }]"
          data-testid="subagent-item"
          :data-agent-id="child.agentId"
        >
          <div class="subagent-info">
            <div class="subagent-name">{{ child.name || child.template }}</div>
            <div class="subagent-task">{{ truncate(child.task, 60) }}</div>
            <div class="subagent-status">{{ child.status }}</div>
          </div>
          <button
            class="subagent-focus-btn"
            data-testid="subagent-focus-button"
            @click="focusChild(child.agentId)"
          >
            View
          </button>
        </li>
      </ul>

      <div v-if="focusedAgentId" class="subagent-focused">
        <button
          class="subagent-back-btn"
          data-testid="subagent-unfocus-button"
          @click="unfocusChild"
        >
          &larr; Back to list
        </button>

        <div class="subagent-transcript" data-testid="subagent-transcript">
          <MessageList :display-items="focusedDisplayItems" :is-loading="isFocusedStreaming" />
        </div>

        <div class="subagent-input" data-testid="subagent-input">
          <ChatInput :streaming="false" @send="handleSend" />
        </div>
      </div>
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
  width: 340px;
  min-width: 340px;
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
  max-height: 40%;
  border-bottom: 1px solid #e0e0e0;
}

.subagent-empty {
  padding: 16px;
  text-align: center;
  color: #666;
  font-size: 13px;
}

.subagent-item {
  display: flex;
  align-items: flex-start;
  gap: 8px;
  padding: 10px 14px;
  border-bottom: 1px solid #e0e0e0;
}

.subagent-item.focused {
  background: #d4e5f7;
  border-left: 3px solid #007bff;
  padding-left: 11px;
}

.subagent-info {
  flex: 1;
  min-width: 0;
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

.subagent-focus-btn {
  flex-shrink: 0;
  padding: 6px 12px;
  background: #007bff;
  color: white;
  border: none;
  border-radius: 6px;
  font-size: 12px;
  font-weight: 500;
  cursor: pointer;
}

.subagent-focus-btn:hover {
  background: #0056b3;
}

.subagent-focused {
  flex: 1;
  display: flex;
  flex-direction: column;
  min-height: 0;
}

.subagent-back-btn {
  align-self: flex-start;
  margin: 8px 14px;
  padding: 6px 12px;
  background: transparent;
  border: 1px solid #ccc;
  border-radius: 6px;
  font-size: 12px;
  color: #666;
  cursor: pointer;
}

.subagent-back-btn:hover {
  background: #e9ecef;
}

.subagent-transcript {
  flex: 1;
  min-height: 0;
  overflow-y: auto;
}
</style>
