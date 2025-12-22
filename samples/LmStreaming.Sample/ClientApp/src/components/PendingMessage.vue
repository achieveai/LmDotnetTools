<script setup lang="ts">
import type { TextMessage } from '@/types';

defineProps<{
  content: TextMessage;
}>();
</script>

<template>
  <div class="pending-message">
    <div class="pending-content">
      <span class="message-text">{{ content.text }}</span>
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

.message-text {
  font-size: 14px;
  line-height: 1.5;
}

.waiting-indicator {
  display: flex;
  align-items: center;
  gap: 4px;
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

