<script setup lang="ts">
import type { ConversationTab } from '@/composables/useConversationTabs';
import { MAIN_TAB_COLOR } from '@/utils/agentColors';

/**
 * Presentational tab strip for the center conversation pane: a `main` tab plus one per sub-agent, each
 * tinted with its assigned color. Emits `select` — all state lives in the parent (`useConversationTabs`).
 */
const props = defineProps<{
  tabs: ConversationTab[];
  activeTabId: string;
}>();

const emit = defineEmits<{ select: [tabId: string] }>();

function hueFor(tab: ConversationTab): string {
  return tab.color ?? MAIN_TAB_COLOR;
}

function tabStyle(tab: ConversationTab): Record<string, string> {
  const hue = hueFor(tab);
  const active = tab.id === props.activeTabId;
  return {
    borderBottomColor: active ? hue : 'transparent',
    background: active ? `color-mix(in srgb, ${hue} 12%, white)` : 'transparent',
    color: active ? hue : '#555',
  };
}
</script>

<template>
  <div class="conversation-tabs" data-testid="conversation-tabs" role="tablist">
    <button
      v-for="tab in tabs"
      :key="tab.id"
      type="button"
      class="conversation-tab"
      :class="{ active: tab.id === activeTabId }"
      role="tab"
      :aria-selected="tab.id === activeTabId"
      data-testid="conversation-tab"
      :data-tab-id="tab.id"
      :title="tab.status ? `${tab.label} · ${tab.status}` : tab.label"
      :style="tabStyle(tab)"
      @click="emit('select', tab.id)"
    >
      <span class="conversation-tab__dot" :style="{ background: hueFor(tab) }" aria-hidden="true" />
      <span class="conversation-tab__label">{{ tab.label }}</span>
    </button>
  </div>
</template>

<style scoped>
.conversation-tabs {
  display: flex;
  align-items: stretch;
  gap: 2px;
  padding: 0 12px;
  border-bottom: 1px solid #e0e0e0;
  background: #fff;
  overflow-x: auto;
  overflow-y: hidden;
  scrollbar-width: thin;
}

.conversation-tab {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  flex-shrink: 0;
  max-width: 200px;
  padding: 8px 12px;
  border: none;
  border-bottom: 2px solid transparent;
  background: transparent;
  font: inherit;
  font-size: 13px;
  font-weight: 500;
  color: #555;
  cursor: pointer;
  white-space: nowrap;
}

.conversation-tab:hover:not(.active) {
  background: #f5f5f5;
}

.conversation-tab.active {
  font-weight: 600;
}

.conversation-tab__dot {
  width: 8px;
  height: 8px;
  border-radius: 50%;
  flex-shrink: 0;
}

.conversation-tab__label {
  overflow: hidden;
  text-overflow: ellipsis;
}
</style>
