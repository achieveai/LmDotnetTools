<script setup lang="ts">
import { inject } from 'vue';
import type { ChatMessage } from '@/composables/useChat';
import type { ToolCallResultMessage, ToolCall } from '@/types';
import { isTextMessage, isToolsCallMessage, isReasoningMessage } from '@/types';
import TextMessageVue from './TextMessage.vue';
import ThinkingPill from './ThinkingPill.vue';
import { getToolComponent } from './tools/toolRegistry';

defineProps<{
  message: ChatMessage;
}>();

// Inject getResultForToolCall from parent (ChatView)
const getResultForToolCall = inject<(toolCallId: string | null | undefined) => ToolCallResultMessage | null>(
  'getResultForToolCall',
  () => null
);

/**
 * Get result for a specific tool call
 */
function getResult(toolCall: ToolCall): ToolCallResultMessage | null {
  return getResultForToolCall(toolCall.tool_call_id);
}
</script>

<template>
  <div class="message-item" :class="message.role">
    <!-- Assistant: avatar on left -->
    <div v-if="message.role === 'assistant'" class="avatar assistant-avatar">
      &#x1F916;
    </div>

    <div class="content">
      <!-- Reasoning/Thinking message - render as ThinkingPill -->
      <ThinkingPill
        v-if="isReasoningMessage(message.content)"
        :message="message.content"
      />

      <!-- Tool calls - dynamically resolved via registry -->
      <template v-else-if="isToolsCallMessage(message.content)">
        <div class="tool-calls-container">
          <component
            v-for="toolCall in message.content.tool_calls"
            :key="toolCall.tool_call_id || toolCall.index"
            :is="getToolComponent(toolCall.function_name)"
            :tool-call="toolCall"
            :result="getResult(toolCall)"
          />
        </div>
      </template>

      <!-- Text message - render as regular text -->
      <TextMessageVue
        v-else-if="isTextMessage(message.content)"
        :message="message.content"
        :is-streaming="message.isStreaming"
      />

      <!-- Fallback for unknown message types -->
      <div v-else class="unknown">
        <pre>{{ JSON.stringify(message.content, null, 2) }}</pre>
      </div>
    </div>

    <!-- User: avatar on right -->
    <div v-if="message.role === 'user'" class="avatar user-avatar">
      &#x1F464;
    </div>
  </div>
</template>

<style scoped>
.message-item {
  display: flex;
  gap: 12px;
  padding: 16px;
  max-width: 85%;
}

/* User messages: right-aligned */
.message-item.user {
  flex-direction: row-reverse;
  margin-left: auto;
  background: #e3f2fd;
  border-radius: 16px 16px 4px 16px;
}

/* Assistant messages: left-aligned */
.message-item.assistant {
  flex-direction: row;
  margin-right: auto;
  background: #f5f5f5;
  border-radius: 16px 16px 16px 4px;
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

.user-avatar {
  background: #1976d2;
}

.assistant-avatar {
  background: #6c757d;
}

.content {
  flex: 1;
  min-width: 0;
}

.tool-calls-container {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
}

.unknown pre {
  background: #f8f9fa;
  padding: 12px;
  border-radius: 8px;
  font-size: 12px;
  overflow-x: auto;
  margin: 0;
}
</style>
