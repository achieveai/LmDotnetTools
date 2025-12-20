<script setup lang="ts">
import { ref } from 'vue';
import type { ToolsCallMessage } from '@/types';

defineProps<{
  message: ToolsCallMessage;
}>();

const expandedCalls = ref<Set<number>>(new Set());

function toggleExpand(index: number) {
  if (expandedCalls.value.has(index)) {
    expandedCalls.value.delete(index);
  } else {
    expandedCalls.value.add(index);
  }
}

function formatArgs(args: string | null | undefined): string {
  if (!args) return '{}';
  try {
    return JSON.stringify(JSON.parse(args), null, 2);
  } catch {
    return args;
  }
}
</script>

<template>
  <div class="tool-call-message">
    <div
      v-for="(call, index) in message.tool_calls"
      :key="call.tool_call_id || index"
      class="tool-call"
    >
      <div class="tool-header" @click="toggleExpand(index)">
        <span class="tool-icon">&#x1F527;</span>
        <span class="tool-name">{{ call.function_name }}</span>
        <span class="expand-icon">{{ expandedCalls.has(index) ? '&#x25BC;' : '&#x25B6;' }}</span>
      </div>
      <div v-if="expandedCalls.has(index)" class="tool-args">
        <pre>{{ formatArgs(call.function_args) }}</pre>
      </div>
    </div>
  </div>
</template>

<style scoped>
.tool-call-message {
  display: flex;
  flex-direction: column;
  gap: 8px;
}

.tool-call {
  border: 1px solid #e0e0e0;
  border-radius: 8px;
  overflow: hidden;
}

.tool-header {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 8px 12px;
  background: #f8f9fa;
  cursor: pointer;
  user-select: none;
}

.tool-header:hover {
  background: #e9ecef;
}

.tool-icon {
  font-size: 14px;
}

.tool-name {
  flex: 1;
  font-family: monospace;
  font-weight: 500;
  color: #6f42c1;
}

.expand-icon {
  font-size: 10px;
  color: #666;
}

.tool-args {
  padding: 12px;
  background: #f8f9fa;
  border-top: 1px solid #e0e0e0;
}

.tool-args pre {
  margin: 0;
  font-size: 12px;
  line-height: 1.4;
  overflow-x: auto;
}
</style>
