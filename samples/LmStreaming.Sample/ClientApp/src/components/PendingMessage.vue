<script setup lang="ts">
import { computed } from 'vue';
import type { TextMessage } from '@/types';
import { parseMarkdown } from '@/utils/markdown';

const props = defineProps<{
  content: TextMessage;
}>();

const parsedText = computed(() => parseMarkdown(props.content.text));
</script>

<template>
  <div class="pending-message">
    <div class="pending-content">
      <div class="markdown-body" v-html="parsedText"></div>
      <div class="waiting-indicator">
        <span class="dot"></span>
        <span class="dot"></span>
        <span class="dot"></span>
      </div>
    </div>
  </div>
</template>

<style scoped>
.pending-message {
  display: flex;
  justify-content: flex-end;
  padding: 8px 16px;
  max-width: 85%;
  margin-left: auto;
}

.pending-content {
  background: #e3f2fd;
  border-radius: 16px 16px 4px 16px;
  padding: 12px 16px;
  display: flex;
  align-items: center;
  gap: 12px;
  opacity: 0.7;
}

.markdown-body {
  font-size: 14px;
  line-height: 1.5;
  word-break: break-word;
}

/* Deep selector for markdown content styles */
.markdown-body :deep(p) {
  margin: 0 0 0.5em 0;
}

.markdown-body :deep(p:last-child) {
  margin-bottom: 0;
}

.waiting-indicator {
  display: flex;
  align-items: center;
  gap: 4px;
  flex-shrink: 0;
}

.dot {
  width: 6px;
  height: 6px;
  border-radius: 50%;
  background: #1976d2;
  animation: pulse 1.4s infinite ease-in-out both;
}

.dot:nth-child(1) {
  animation-delay: -0.32s;
}

.dot:nth-child(2) {
  animation-delay: -0.16s;
}

@keyframes pulse {
  0%, 80%, 100% {
    transform: scale(0.6);
    opacity: 0.3;
  }
  40% {
    transform: scale(1);
    opacity: 1;
  }
}
</style>

