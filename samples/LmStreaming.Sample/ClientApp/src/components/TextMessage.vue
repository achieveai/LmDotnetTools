<script setup lang="ts">
import { computed } from 'vue';
import type { TextMessage } from '@/types';
import { parseMarkdown } from '@/utils/markdown';

const props = withDefaults(
  defineProps<{
    message: TextMessage;
    isStreaming?: boolean;
    /**
     * `false` while this message is still being streamed into. Syntax highlighting is skipped
     * for the growing text (it re-parses on every delta) and applied once the run completes.
     * Distinct from `isStreaming`, which only controls the blinking cursor.
     */
    isComplete?: boolean;
  }>(),
  { isComplete: true }
);

const parsedText = computed(() =>
  parseMarkdown(props.message.text, { highlight: props.isComplete !== false })
);
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

/* Markdown element styling lives in assets/markdown.css (shared with PendingMessage). */

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
