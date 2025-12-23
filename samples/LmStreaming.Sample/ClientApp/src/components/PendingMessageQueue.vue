<script setup lang="ts">
import type { TextMessage } from '@/types';

export interface PendingItem {
  id: string;
  content: TextMessage;
  timestamp: number;
}

defineProps<{
  pendingMessages: PendingItem[];
}>();
</script>

<template>
  <div v-if="pendingMessages.length > 0" class="pending-queue">
    <div class="pending-header">
      <span class="waiting-icon">⏳</span>
      <span class="header-text">Waiting to send...</span>
    </div>
    <div class="pending-list">
      <div 
        v-for="msg in pendingMessages" 
        :key="msg.id" 
        class="pending-item"
      >
        <span class="item-icon">📤</span>
        <span class="item-text">{{ msg.content.text }}</span>
      </div>
    </div>
  </div>
</template>

<style scoped>
.pending-queue {
  border-top: 1px solid #e0e0e0;
  background: #f9f9f9;
  padding: 8px 16px;
}

.pending-header {
  display: flex;
  align-items: center;
  gap: 8px;
  margin-bottom: 8px;
  font-size: 12px;
  color: #666;
}

.waiting-icon {
  font-size: 14px;
  animation: pulse 1.5s ease-in-out infinite;
}

@keyframes pulse {
  0%, 100% {
    opacity: 1;
  }
  50% {
    opacity: 0.5;
  }
}

.header-text {
  font-weight: 600;
}

.pending-list {
  display: flex;
  flex-direction: column;
  gap: 6px;
}

.pending-item {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 8px 12px;
  background: #fff;
  border: 1px solid #e0e0e0;
  border-radius: 8px;
  font-size: 13px;
}

.item-icon {
  font-size: 14px;
  flex-shrink: 0;
}

.item-text {
  flex: 1;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  color: #333;
}
</style>


