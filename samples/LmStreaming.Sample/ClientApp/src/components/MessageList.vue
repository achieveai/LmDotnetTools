<script setup lang="ts">
import type { DisplayItem } from '@/types';
import TextMessage from './TextMessage.vue';
import MetadataPill from './MetadataPill.vue';
import PendingMessage from './PendingMessage.vue';

defineProps<{
  displayItems: readonly DisplayItem[];
}>();
</script>

<template>
  <div class="message-list">
    <div v-if="displayItems.length === 0" class="empty-state">
      <p>No messages yet. Send a message to start the conversation.</p>
    </div>
    
    <template v-for="item in displayItems" :key="item.id">
      <!-- User message (pending or active) -->
      <div v-if="item.type === 'user-message'" class="user-message-wrapper">
        <div class="user-message-container">
          <div class="user-avatar">&#x1F464;</div>
          <div class="user-content">
            <PendingMessage v-if="item.status === 'pending'" :content="item.content" />
            <TextMessage v-else :message="item.content" :is-streaming="false" />
          </div>
        </div>
      </div>
      
      <!-- Assistant message with pill -->
      <div v-else-if="item.type === 'pill'" class="assistant-message-wrapper">
        <div class="assistant-message-container">
          <div class="assistant-avatar">&#x1F916;</div>
          <div class="assistant-content">
            <MetadataPill :items="item.items" />
          </div>
        </div>
      </div>
      
      <!-- Assistant text message -->
      <div v-else-if="item.type === 'assistant-message'" class="assistant-message-wrapper">
        <div class="assistant-message-container">
          <div class="assistant-avatar">&#x1F916;</div>
          <div class="assistant-content">
            <div class="text-bubble">
              <TextMessage :message="item.content" :is-streaming="false" />
            </div>
          </div>
        </div>
      </div>
    </template>
  </div>
</template>

<style scoped>
.message-list {
  flex: 1;
  overflow-y: auto;
  display: flex;
  flex-direction: column;
  padding: 16px;
  gap: 12px;
}

.empty-state {
  flex: 1;
  display: flex;
  align-items: center;
  justify-content: center;
  color: #666;
  font-size: 14px;
}

.user-message-wrapper,
.assistant-message-wrapper {
  display: flex;
  max-width: 85%;
}

.user-message-wrapper {
  margin-left: auto;
}

.assistant-message-wrapper {
  margin-right: auto;
}

.user-message-container,
.assistant-message-container {
  display: flex;
  gap: 12px;
  align-items: flex-start;
}

.user-message-container {
  flex-direction: row-reverse;
}

.user-avatar,
.assistant-avatar {
  width: 40px;
  height: 40px;
  border-radius: 50%;
  display: flex;
  align-items: center;
  justify-content: center;
  font-size: 20px;
  flex-shrink: 0;
}

.user-avatar {
  background: #1976d2;
}

.assistant-avatar {
  background: #6c757d;
}

.user-content,
.assistant-content {
  flex: 1;
  min-width: 0;
}

.text-bubble {
  background: #ffffff;
  border: 1px solid #e0e0e0;
  border-radius: 16px 16px 16px 4px;
  padding: 12px 16px;
}

/* User message styling handled in PendingMessage and TextMessage components */
</style>
