<script setup lang="ts">
import { nextTick, watch, type ComponentPublicInstance } from 'vue';
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
const tabElements = new Map<string, HTMLElement>();

function hueFor(tab: ConversationTab): string {
  return tab.color ?? MAIN_TAB_COLOR;
}

/**
 * Tooltip text: prefix workflow tabs with "Workflow: " so their kind reads even when the badge is off-screen, and
 * name an errored child's failure code (e.g. "error (view_exceeds_window)") so a size refusal is not a bare "error".
 */
function tabTitle(tab: ConversationTab): string {
  const base = tab.kind === 'workflow' ? `Workflow: ${tab.label}` : tab.label;
  if (!tab.status) return base;
  const status = tab.status === 'error' && tab.failureCode ? `error (${tab.failureCode})` : tab.status;
  return `${base} · ${status}`;
}

function setTabElement(
  tabId: string,
  element: Element | ComponentPublicInstance | null
): void {
  let htmlElement: HTMLElement | null = null;
  if (element instanceof HTMLElement) {
    htmlElement = element;
  } else if (element && '$el' in element && element.$el instanceof HTMLElement) {
    htmlElement = element.$el;
  }
  if (htmlElement) tabElements.set(tabId, htmlElement);
  else tabElements.delete(tabId);
}

watch(
  () => [props.activeTabId, ...props.tabs.map((tab) => tab.id)],
  async () => {
    await nextTick();
    tabElements
      .get(props.activeTabId)
      ?.scrollIntoView?.({ block: 'nearest', inline: 'nearest' });
  },
  { immediate: true }
);
</script>

<template>
  <div class="conversation-tabs" data-testid="conversation-tabs" role="tablist">
    <button
      v-for="tab in tabs"
      :key="tab.id"
      :ref="(element) => setTabElement(tab.id, element)"
      type="button"
      class="conversation-tab"
      :class="{ active: tab.id === activeTabId }"
      role="tab"
      :aria-selected="tab.id === activeTabId"
      data-testid="conversation-tab"
      :data-tab-id="tab.id"
      :data-tab-kind="tab.kind"
      :title="tabTitle(tab)"
      @click="emit('select', tab.id)"
    >
      <span class="conversation-tab__dot" :style="{ background: hueFor(tab) }" aria-hidden="true" />
      <span
        v-if="tab.kind === 'workflow'"
        class="conversation-tab__badge"
        data-testid="workflow-tab-badge"
        title="Workflow run"
        aria-label="Workflow run"
        >⚙</span
      >
      <span class="conversation-tab__label">{{ tab.label }}</span>
    </button>
  </div>
</template>

<style scoped>
.conversation-tabs {
  display: flex;
  align-items: stretch;
  gap: 3px;
  padding: 4px 10px;
  border-bottom: 1px solid #e0e0e0;
  background: #fff;
  overflow-x: auto;
  overflow-y: hidden;
  scrollbar-width: thin;
  scrollbar-color: transparent transparent;
  overscroll-behavior-x: contain;
}

.conversation-tabs::-webkit-scrollbar {
  height: 6px;
}

.conversation-tabs::-webkit-scrollbar-track {
  background: transparent;
}

.conversation-tabs::-webkit-scrollbar-thumb {
  border-radius: 999px;
  background: transparent;
}

.conversation-tabs:hover,
.conversation-tabs:focus-within {
  scrollbar-color: #cbd1d8 transparent;
}

.conversation-tabs:hover::-webkit-scrollbar-thumb,
.conversation-tabs:focus-within::-webkit-scrollbar-thumb {
  background: #cbd1d8;
}

.conversation-tab {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  flex-shrink: 0;
  max-width: 160px;
  min-width: 0;
  padding: 5px 8px;
  border: none;
  border-radius: 6px;
  background: transparent;
  font: inherit;
  font-size: 13px;
  font-weight: 400;
  color: #5f6874;
  cursor: pointer;
  white-space: nowrap;
}

.conversation-tab:hover:not(.active) {
  background: #f3f4f5;
}

.conversation-tab.active {
  background: #e9ecef;
  color: #343a40;
  font-weight: 500;
}

.conversation-tab:focus-visible {
  outline: 2px solid #2d6cdf;
  outline-offset: 1px;
}

.conversation-tab__dot {
  width: 7px;
  height: 7px;
  border-radius: 50%;
  flex-shrink: 0;
}

.conversation-tab__badge {
  flex-shrink: 0;
  font-size: 11px;
  line-height: 1;
  opacity: 0.8;
}

.conversation-tab__label {
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
}
</style>
