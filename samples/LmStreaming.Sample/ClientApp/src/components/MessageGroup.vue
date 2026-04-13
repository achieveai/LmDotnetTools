<script setup lang="ts">
import { inject } from 'vue';
import type { ChatMessage } from '@/composables/useChat';
import type { ToolCallResultMessage, ToolCall } from '@/types';
import { isTextMessage, isToolsCallMessage, isReasoningMessage } from '@/types';
import TextMessageVue from './TextMessage.vue';
import ThinkingPill from './ThinkingPill.vue';
import { getToolComponent } from './tools/toolRegistry';

const props = defineProps<{
  group: ChatMessage[];
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

// Separate metadata (thinking/tools) from text responses
const metadataMessages = props.group.filter(
  (msg) => isReasoningMessage(msg.content) || isToolsCallMessage(msg.content)
);
const textMessages = props.group.filter((msg) => isTextMessage(msg.content));
const role = props.group[0]?.role || 'assistant';
</script>

<template>
  <div class="message-group" :class="role">
    <!-- Assistant: avatar on left -->
    <div v-if="role === 'assistant'" class="avatar assistant-avatar">
      &#x1F916;
    </div>

    <div class="group-content">
      <!-- Metadata Container (Gray Box) for thinking and tool calls -->
      <div v-if="metadataMessages.length > 0" class="metadata-container">
        <template v-for="message in metadataMessages" :key="message.id">
          <!-- Reasoning/Thinking message -->
          <ThinkingPill
            v-if="isReasoningMessage(message.content)"
            :message="message.content"
          />

          <!-- Tool calls -->
          <template v-else-if="isToolsCallMessage(message.content)">
            <component
              v-for="toolCall in message.content.tool_calls"
              :key="toolCall.tool_call_id || toolCall.index"
              :is="getToolComponent(toolCall.function_name)"
              :tool-call="toolCall"
              :result="getResult(toolCall)"
            />
          </template>
        </template>
      </div>

      <!-- Text Response Container -->
      <div v-if="textMessages.length > 0" class="text-container" :class="role">
        <template v-for="message in textMessages" :key="message.id">
          <TextMessageVue
            v-if="isTextMessage(message.content)"
            :message="message.content"
            :is-streaming="message.isStreaming"
          />
        </template>
      </div>
    </div>

    <!-- User: avatar on right -->
    <div v-if="role === 'user'" class="avatar user-avatar">
      &#x1F464;
    </div>
  </div>
</template>

<style scoped>
.message-group {
  display: flex;
  gap: 12px;
  padding: 16px;
  max-width: 85%;
  margin-bottom: 12px;
}

/* User messages: right-aligned */
.message-group.user {
  flex-direction: row-reverse;
  margin-left: auto;
}

/* Assistant messages: left-aligned */
.message-group.assistant {
  flex-direction: row;
  margin-right: auto;
}

.avatar {
  width: 40px;
  height: 40px;
  border-radius: 50%;
  display: flex;
  align-items: center;
  justify-content: center;
  font-size: 20px;
  flex-shrink: 0;
  align-self: flex-start;
}

.user-avatar {
  background: #1976d2;
}

.assistant-avatar {
  background: #6c757d;
}

.group-content {
  flex: 1;
  min-width: 0;
  display: flex;
  flex-direction: column;
  gap: 8px;
}

/* Metadata Container (Gray Box) */
.metadata-container {
  background: #f0f0f0;
  border-radius: 12px;
  padding: 12px;
  border: 1px solid #e0e0e0;
  display: flex;
  flex-direction: column;
  gap: 8px;
}

/* Text Container */
.text-container {
  padding: 12px 16px;
  border-radius: 16px;
  line-height: 1.5;
}

.text-container.user {
  background: #e3f2fd;
  border-radius: 16px 16px 4px 16px;
  align-self: flex-end;
}

.text-container.assistant {
  background: #ffffff;
  border: 1px solid #e0e0e0;
  border-radius: 16px 16px 16px 4px;
}
</style>
