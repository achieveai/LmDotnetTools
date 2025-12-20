<script setup lang="ts">
import type { TextMessage } from '@/types';

defineProps<{
  message: TextMessage;
  isStreaming?: boolean;
}>();
</script>

<template>
  <div class="text-message" :class="{ thinking: message.isThinking }">
    <span class="text">{{ message.text }}</span>
    <span v-if="isStreaming" class="cursor">|</span>
  </div>
</template>

<style scoped>
.text-message {
  white-space: pre-wrap;
  word-break: break-word;
  line-height: 1.5;
}

.text-message.thinking {
  font-style: italic;
  color: #666;
}

.cursor {
  animation: blink 1s infinite;
  color: #007bff;
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
