<script setup lang="ts">
import { ref, watch, nextTick } from 'vue';
import type { ReasoningMessage, ToolsCallMessage } from '@/types';
import { isReasoningMessage, isToolsCallMessage, normalizeReasoningVisibility } from '@/types';
import { truncateText } from '@/utils';
import ToolPill from '@/components/ToolPill.vue';

const props = defineProps<{
  items: Array<ReasoningMessage | ToolsCallMessage>;
}>();

// Track which reasoning items are expanded (tool pills own their own expansion).
const expandedItems = ref<Set<number>>(new Set());

// Track if the entire pill is expanded (the "Show all N items" affordance).
const isPillExpanded = ref(false);

// Reference to the scrollable container.
const pillItemsContainer = ref<HTMLElement | null>(null);

// Auto-scroll to bottom when new items are added (only when collapsed).
watch(
  () => props.items.length,
  async () => {
    if (!isPillExpanded.value) {
      await nextTick();
      if (pillItemsContainer.value) {
        pillItemsContainer.value.scrollTop = pillItemsContainer.value.scrollHeight;
      }
    }
  }
);

function togglePillExpansion(event: Event) {
  event.stopPropagation();
  isPillExpanded.value = !isPillExpanded.value;
}

function toggleExpand(index: number) {
  if (expandedItems.value.has(index)) {
    expandedItems.value.delete(index);
  } else {
    expandedItems.value.add(index);
  }
}

function getReasoningSummary(item: ReasoningMessage): string {
  if (isEncryptedReasoning(item)) {
    return 'Encrypted reasoning';
  }
  return truncateText(item.reasoning, 60);
}

function isEncryptedReasoning(item: ReasoningMessage): boolean {
  return normalizeReasoningVisibility(item.visibility) === 'Encrypted';
}
</script>

<template>
  <div class="metadata-pill" data-testid="metadata-pill">
    <!-- Pill header with expand/collapse button -->
    <div v-if="props.items.length > 3" class="pill-header" @click="togglePillExpansion">
      <span class="pill-expand-icon">{{ isPillExpanded ? '▼' : '▶' }}</span>
      <span class="pill-header-text">
        {{ isPillExpanded ? 'Collapse' : `Show all ${props.items.length} items` }}
      </span>
    </div>

    <div class="pill-items" :class="{ expanded: isPillExpanded }" ref="pillItemsContainer">
      <template v-for="(item, index) in props.items" :key="index">
        <!-- Reasoning stays INLINE (locked by MetadataPillEncryptedReasoning.test) -->
        <div
          v-if="isReasoningMessage(item)"
          class="pill-item"
          :class="{ expanded: expandedItems.has(index) }"
          data-testid="thinking-pill"
          @click="toggleExpand(index)"
        >
          <div class="item-header">
            <span class="item-icon">💭</span>
            <span class="item-label">Thinking:</span>
            <span class="item-summary">{{ getReasoningSummary(item) }}</span>
            <span class="expand-icon">{{ expandedItems.has(index) ? '▼' : '▶' }}</span>
          </div>
          <div v-if="expandedItems.has(index)" class="item-content">
            <pre class="reasoning-text">{{
              isEncryptedReasoning(item) ? '[Encrypted reasoning hidden]' : item.reasoning
            }}</pre>
          </div>
        </div>

        <!-- Tool calls delegate to ToolPill — one pill per tool_call -->
        <template v-else-if="isToolsCallMessage(item)">
          <ToolPill
            v-for="(toolCall, tcIndex) in item.tool_calls"
            :key="`${index}-${tcIndex}`"
            :tool-call="toolCall"
          />
        </template>
      </template>
    </div>
  </div>
</template>

<style scoped>
.metadata-pill {
  background: #f0f0f0;
  border-radius: 12px;
  padding: 8px;
  border: 1px solid #e0e0e0;
  margin-bottom: 8px;
  overflow: hidden;
}

.pill-header {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 6px 8px;
  margin-bottom: 8px;
  background: #e8e8e8;
  border-radius: 8px;
  cursor: pointer;
  user-select: none;
  transition: background 0.2s ease;
}

.pill-header:hover {
  background: #d8d8d8;
}

.pill-expand-icon {
  font-size: 10px;
  color: #666;
}

.pill-header-text {
  font-size: 12px;
  font-weight: 600;
  color: #555;
}

.pill-items {
  display: flex;
  flex-direction: column;
  gap: 4px;
  max-height: 150px; /* Height for ~3 collapsed items */
  overflow-y: auto;
  overflow-x: hidden;
  transition: max-height 0.3s ease;
}

.pill-items.expanded {
  max-height: none;
  overflow-y: visible;
}

.pill-items::-webkit-scrollbar {
  width: 6px;
}

.pill-items::-webkit-scrollbar-track {
  background: #f0f0f0;
  border-radius: 3px;
}

.pill-items::-webkit-scrollbar-thumb {
  background: #c0c0c0;
  border-radius: 3px;
}

.pill-items::-webkit-scrollbar-thumb:hover {
  background: #a0a0a0;
}

/* Reasoning pill (tool pills style themselves) */
.pill-item {
  background: #fff;
  border-radius: 8px;
  padding: 8px 12px;
  cursor: pointer;
  transition: all 0.2s ease;
  border: 1px solid transparent;
  min-width: 0;
}

.pill-item:hover {
  background: #f8f8f8;
  border-color: #d0d0d0;
}

.item-header {
  display: flex;
  align-items: center;
  gap: 8px;
  font-size: 14px;
  user-select: none;
}

.item-icon {
  font-size: 16px;
  flex-shrink: 0;
}

.item-label {
  font-weight: 600;
  color: #333;
  flex-shrink: 0;
}

.item-summary {
  color: #666;
  flex: 1;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.expand-icon {
  color: #999;
  font-size: 10px;
  flex-shrink: 0;
  margin-left: auto;
}

.item-content {
  margin-top: 12px;
  padding-top: 12px;
  border-top: 1px solid #e0e0e0;
  overflow-x: auto;
}

.reasoning-text {
  margin: 0;
  padding: 12px;
  background: #f8f9fa;
  border-radius: 6px;
  font-size: 13px;
  line-height: 1.5;
  white-space: pre-wrap;
  word-wrap: break-word;
  overflow-x: auto;
}
</style>
