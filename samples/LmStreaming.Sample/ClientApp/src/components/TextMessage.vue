<script setup lang="ts">
import { computed } from 'vue';
import type { TextMessage } from '@/types';
import { parseMarkdown } from '@/utils/markdown';

const props = defineProps<{
  message: TextMessage;
  isStreaming?: boolean;
}>();

const parsedText = computed(() => parseMarkdown(props.message.text));
</script>

<template>
  <div class="text-message" :class="{ thinking: message.isThinking }">
    <div class="markdown-content" v-html="parsedText"></div>
    <span v-if="isStreaming" class="cursor">|</span>
  </div>
</template>

<style scoped>
.text-message {
  line-height: 1.5;
  position: relative;
}

.markdown-content {
  overflow-wrap: break-word;
}

/* Deep selector for markdown content styles */
.markdown-content :deep(p) {
  margin: 0 0 1em 0;
}

.markdown-content :deep(p:last-child) {
  margin-bottom: 0;
}

.markdown-content :deep(pre) {
  background: #f4f4f4;
  padding: 10px;
  border-radius: 4px;
  overflow-x: auto;
}

.markdown-content :deep(code) {
  font-family: monospace;
  background: rgba(0, 0, 0, 0.05);
  padding: 2px 4px;
  border-radius: 3px;
}

.text-message.thinking {
  font-style: italic;
  color: #666;
}

.cursor {
  display: inline-block;
  animation: blink 1s infinite;
  color: #007bff;
  margin-left: 2px;
  vertical-align: text-bottom;
}

@keyframes blink {
  0%, 50% {
    opacity: 1;
  }
  51%, 100% {
    opacity: 0;
  }
}
</style>
