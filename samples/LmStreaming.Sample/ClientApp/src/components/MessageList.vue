<script setup lang="ts">
import type { ChatMessage } from '@/composables/useChat';
import MessageItem from './MessageItem.vue';

defineProps<{
  messages: readonly ChatMessage[];
}>();
</script>

<template>
  <div class="message-list">
    <div v-if="messages.length === 0" class="empty-state">
      <p>No messages yet. Send a message to start the conversation.</p>
    </div>
    <MessageItem
      v-for="message in messages"
      :key="message.id"
      :message="message"
    />
  </div>
</template>

<style scoped>
.message-list {
  flex: 1;
  overflow-y: auto;
  display: flex;
  flex-direction: column;
}

.empty-state {
  flex: 1;
  display: flex;
  align-items: center;
  justify-content: center;
  color: #666;
  font-size: 14px;
}
</style>
