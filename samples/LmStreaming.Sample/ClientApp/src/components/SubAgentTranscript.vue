<script setup lang="ts">
import { provide } from 'vue';
import type { DisplayItem, ToolCallResultMessage } from '@/types';
import { GET_RESULT_FOR_TOOL_CALL } from '@/composables/useToolResult';
import { SUBMIT_CLIENT_TOOL_RESULT, type ClientToolSubmitFn } from '@/composables/useClientToolSubmit';
import MessageList from './MessageList.vue';
import ChatInput from './ChatInput.vue';
import PendingQuestionDock from './PendingQuestionDock.vue';

/**
 * The center-pane view for the active sub-agent tab. Relocates the transcript + reply input + error
 * banner that used to live inside `SubAgentListPanel`, driven by the shared `useSubAgentPanel` surface
 * passed in as props. Streams live only for the active tab (connect-on-activate).
 */
const props = defineProps<{
  /** The sub-agent tab currently active (the focus target); also the MessageList remount key. */
  activeAgentId: string;
  /** The sub-agent whose live stream is actually attached (null until focus completes). */
  focusedAgentId: string | null;
  displayItems: DisplayItem[];
  isStreaming: boolean;
  error: string | null;
  /** Child-scoped tool-result resolver (this sub-agent's results, not the parent chat's). */
  getResultForToolCall: (toolCallId: string | null | undefined) => ToolCallResultMessage | null;
  /**
   * Child-scoped deferred client-tool submit (#246 defect 1): submits over the FOCUSED sub-agent's
   * own `/ws/subagent` connection (`useSubAgentPanel.submitToFocusedChild`), not the root chat's
   * connection — the root does not know a descendant's toolCallId and would reply `not_found`.
   */
  submitClientToolResult: ClientToolSubmitFn;
}>();

const emit = defineEmits<{ send: [text: string] }>();

// Shadow ChatLayout's provide for THIS subtree so nested tool pills resolve against the child's
// results — identical to the override SubAgentListPanel used to do. The resolver reads live state at
// call time, so a stable identity provided once stays correct across focus changes.
provide(GET_RESULT_FOR_TOOL_CALL, props.getResultForToolCall);
// Shadow ChatLayout's root SUBMIT_CLIENT_TOOL_RESULT so a descendant's AskUserQuestion (rendered via
// QuestionRich inside this subtree) answers over the FOCUSED CHILD connection, not the root (#246).
provide(SUBMIT_CLIENT_TOOL_RESULT, props.submitClientToolResult);
</script>

<template>
  <div class="subagent-view" data-testid="subagent-view">
    <div v-if="error" class="subagent-view__error" data-testid="subagent-error" role="alert">
      {{ error }}
    </div>
    <div class="subagent-view__transcript" data-testid="subagent-transcript">
      <MessageList :key="activeAgentId" :display-items="displayItems" :is-loading="isStreaming" />
    </div>
    <!-- Inside this subtree on purpose: it inherits the two provides shadowed above, so a
         descendant's question resolves against the CHILD's results and answers over the CHILD's
         socket. -->
    <PendingQuestionDock :display-items="displayItems" />
    <!-- Send-only: never streaming (so no Stop button) — a reply resumes a completed child. Disabled
         until the live connection for this exact tab is attached, so a send can't drop on a dead socket. -->
    <ChatInput
      :streaming="false"
      :disabled="focusedAgentId !== activeAgentId"
      @send="emit('send', $event)"
    />
  </div>
</template>

<style scoped>
.subagent-view {
  flex: 1;
  display: flex;
  flex-direction: column;
  min-height: 0;
}

.subagent-view__error {
  padding: 10px 16px;
  background: #fdecea;
  color: #b3261e;
  font-size: 13px;
  border-bottom: 1px solid #f5c6cb;
}

.subagent-view__transcript {
  flex: 1;
  min-height: 0;
  display: flex;
  flex-direction: column;
}
</style>
