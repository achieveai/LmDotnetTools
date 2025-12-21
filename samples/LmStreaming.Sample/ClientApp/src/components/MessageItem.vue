<script setup lang="ts">
import type { ChatMessage } from '@/composables/useChat';
import { isTextMessage, isToolsCallMessage, isReasoningMessage } from '@/types';
import TextMessageVue from './TextMessage.vue';
import ToolCallMessage from './ToolCallMessage.vue';

defineProps<{
  message: ChatMessage;
}>();
</script>

<template>
  <div class="message-item" :class="message.role">
    <div class="avatar">
      {{ message.role === 'user' ? '&#x1F464;' : '&#x1F916;' }}
    </div>
    <div class="content">
      <div class="role-label">{{ message.role }}</div>

      <!-- Text message -->
      <TextMessageVue
        v-if="isTextMessage(message.content)"
        :message="message.content"
        :is-streaming="message.isStreaming"
      />

      <!-- Tool call message -->
      <ToolCallMessage
        v-else-if="isToolsCallMessage(message.content)"
        :message="message.content"
      />

      <!-- Reasoning message -->
      <div v-else-if="isReasoningMessage(message.content)" class="reasoning">
        <details>
          <summary>Reasoning</summary>
          <pre>{{ message.content.reasoning }}</pre>
        </details>
      </div>

      <!-- Fallback for unknown message types -->
      <div v-else class="unknown">
        <pre>{{ JSON.stringify(message.content, null, 2) }}</pre>
      </div>
    </div>
  </div>
</template>

<style scoped>
.message-item {
  display: flex;
  gap: 12px;
  padding: 16px;
}

.message-item.user {
  background: #f0f7ff;
}

.message-item.assistant {
  background: #fff;
}

.avatar {
  width: 36px;
  height: 36px;
  border-radius: 50%;
  display: flex;
  align-items: center;
  justify-content: center;
  font-size: 18px;
  flex-shrink: 0;
}

.user .avatar {
  background: #007bff;
}

.assistant .avatar {
  background: #6c757d;
}

.content {
  flex: 1;
  min-width: 0;
}

.role-label {
  font-size: 12px;
  font-weight: 600;
  text-transform: capitalize;
  color: #666;
  margin-bottom: 4px;
}

.reasoning details {
  background: #f8f9fa;
  border-radius: 8px;
  padding: 8px 12px;
}

.reasoning summary {
  cursor: pointer;
  font-weight: 500;
  color: #6f42c1;
}

.reasoning pre {
  margin: 8px 0 0;
  font-size: 13px;
  white-space: pre-wrap;
  word-break: break-word;
}

.unknown pre {
  background: #f8f9fa;
  padding: 12px;
  border-radius: 8px;
  font-size: 12px;
  overflow-x: auto;
}
</style>
