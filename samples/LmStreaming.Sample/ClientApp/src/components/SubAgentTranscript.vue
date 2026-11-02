<script setup lang="ts">
import { computed, nextTick, provide, ref } from 'vue';
import type { DisplayItem, ToolCallResultMessage } from '@/types';
import type { ViewPreference } from '@/composables/useViewPreference';
import { GET_RESULT_FOR_TOOL_CALL, IS_QUESTION_ANSWERED } from '@/composables/useToolResult';
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
  /** Child-scoped "already answered" lookup for early-settled questions (see `isQuestionAwaitingAnswer`). */
  isQuestionAnswered?: (toolCallId: string) => boolean;
  /**
   * Child-scoped deferred client-tool submit (#246 defect 1): submits over the FOCUSED sub-agent's
   * own `/ws/subagent` connection (`useSubAgentPanel.submitToFocusedChild`), not the root chat's
   * connection — the root does not know a descendant's toolCallId and would reply `not_found`.
   */
  submitClientToolResult: ClientToolSubmitFn;
  viewPreference: ViewPreference;
  questionScope?: string;
  questionSource?: string;
  requestedQuestionId?: string;
}>();

const emit = defineEmits<{ send: [text: string]; questionBusy: [busy: boolean]; questionOpen: [open: boolean]; questionOpened: [id: string] }>();

// Shadow ChatLayout's provide for THIS subtree so nested tool pills resolve against the child's
// results — identical to the override SubAgentListPanel used to do. The resolver reads live state at
// call time, so a stable identity provided once stays correct across focus changes.
provide(GET_RESULT_FOR_TOOL_CALL, props.getResultForToolCall);
provide(IS_QUESTION_ANSWERED, props.isQuestionAnswered ?? (() => false));
// Shadow ChatLayout's root SUBMIT_CLIENT_TOOL_RESULT so a descendant's AskUserQuestion (rendered via
// QuestionRich inside this subtree) answers over the FOCUSED CHILD connection, not the root (#246).
provide(SUBMIT_CLIENT_TOOL_RESULT, props.submitClientToolResult);
const safeAgentId = (value: string) => value.replace(/[^a-zA-Z0-9_-]/g, '-');

const chatInputRef = ref<InstanceType<typeof ChatInput> | null>(null);
const composerEmpty = computed(() => (chatInputRef.value?.getText() ?? '').trim() === '');

/**
 * Replies that could not be sent while a newer draft held the composer (#868). Each stays here until
 * the user puts it back or dismisses it: a later error replaces the banner, never this list. It lives
 * as long as the composer does, which ChatLayout remounts per conversation and tab.
 */
const unsentTexts = ref<{ id: number; text: string }[]>([]);
let unsentSeq = 0;

/**
 * Gives a reply that could not be sent back to the user (#868). It goes into the composer when that
 * is empty or still holds it; a newer draft is never overwritten, and the text joins the unsent list.
 */
function keepUnsentText(text: string): void {
  const input = chatInputRef.value;
  const draft = input?.getText() ?? '';
  if (input && (draft.trim() === '' || draft === text)) {
    input.setText(text);
    return;
  }
  unsentTexts.value.push({ id: ++unsentSeq, text });
}

const unsentSectionRef = ref<HTMLElement | null>(null);

/** Removes an entry. The button that held focus goes with its row, so focus moves on, never to the page. */
async function removeUnsent(id: number, focusComposer: boolean): Promise<void> {
  const index = unsentTexts.value.findIndex((e) => e.id === id);
  if (index < 0) return;
  unsentTexts.value.splice(index, 1);
  await nextTick();
  // Dismiss stays enabled whatever the draft holds, so the row that slid into place can always take focus.
  const dismissButtons = unsentSectionRef.value?.querySelectorAll<HTMLButtonElement>(
    '[data-testid="subagent-unsent-dismiss"]'
  );
  const next = dismissButtons?.[Math.min(index, dismissButtons.length - 1)];
  if (!focusComposer && next) next.focus();
  else chatInputRef.value?.focus();
}

function putBack(id: number): void {
  const entry = unsentTexts.value.find((e) => e.id === id);
  if (!entry || !chatInputRef.value || !composerEmpty.value) return;
  chatInputRef.value.setText(entry.text);
  void removeUnsent(id, true);
}

function dismiss(id: number): void {
  void removeUnsent(id, false);
}

const unsentHintId = computed(() => `subagent-unsent-hint-${safeAgentId(props.activeAgentId)}`);

defineExpose({ keepUnsentText });
</script>

<template>
  <div :id="`conversation-agent-view-${safeAgentId(activeAgentId)}`" class="subagent-view"
    data-testid="subagent-view" role="region" :aria-labelledby="`conversation-agent-selector-${safeAgentId(activeAgentId)}`">
    <div v-if="error" class="subagent-view__error" data-testid="subagent-error" role="alert">
      {{ error }}
    </div>
    <div class="subagent-view__transcript" data-testid="subagent-transcript">
      <MessageList
        :key="activeAgentId"
        :display-items="displayItems"
        :is-loading="isStreaming"
        :view-preference="viewPreference"
      />
    </div>
    <!-- Inside this subtree on purpose: it inherits the two provides shadowed above, so a
         descendant's question resolves against the CHILD's results and answers over the CHILD's
         socket. -->
    <PendingQuestionDock :display-items="focusedAgentId === activeAgentId ? displayItems : []"
      :scope-key="questionScope || activeAgentId" :source-label="questionSource || 'Agent'"
      :active="focusedAgentId === activeAgentId" :requested-question-id="requestedQuestionId"
      @busy-change="emit('questionBusy', $event)" @open-change="emit('questionOpen', $event)" @opened="emit('questionOpened', $event)" />
    <!-- Apart from the error banner on purpose, so a later error cannot take an unsent reply away. -->
    <section v-if="unsentTexts.length > 0" ref="unsentSectionRef" class="subagent-view__unsent" data-testid="subagent-unsent"
      aria-label="Messages not sent">
      <p class="subagent-view__unsent-title" role="status">Not sent ({{ unsentTexts.length }})</p>
      <ul class="subagent-view__unsent-list">
        <li v-for="(entry, index) in unsentTexts" :key="entry.id" class="subagent-view__unsent-item"
          data-testid="subagent-unsent-item">
          <span class="subagent-view__unsent-text" data-testid="subagent-unsent-text">{{ entry.text }}</span>
          <button type="button" data-testid="subagent-unsent-put-back" :disabled="!composerEmpty"
            :aria-label="`Put back unsent message ${index + 1}`"
            :aria-describedby="composerEmpty ? undefined : unsentHintId" @click="putBack(entry.id)">Put back</button>
          <button type="button" data-testid="subagent-unsent-dismiss" :aria-label="`Dismiss unsent message ${index + 1}`"
            @click="dismiss(entry.id)">Dismiss</button>
        </li>
      </ul>
      <p v-if="!composerEmpty" :id="unsentHintId" class="subagent-view__unsent-hint">
        Your draft was kept. Send or clear it to put a message back.
      </p>
    </section>
    <!-- Send-only: never streaming (so no Stop button) — a reply resumes a completed child. Disabled
         until the live connection for this exact tab is attached, so a send can't drop on a dead socket. -->
    <ChatInput
      ref="chatInputRef"
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

.subagent-view__unsent {
  padding: 8px 16px;
  background: #fff8e1;
  color: #5f4b00;
  font-size: 13px;
  border-top: 1px solid #f0d58c;
}

.subagent-view__unsent-title,
.subagent-view__unsent-hint {
  margin: 0;
}

/* Only the list scrolls: the title, the hint and the composer below stay in view however long it gets. */
.subagent-view__unsent-list {
  list-style: none;
  margin: 4px 0;
  padding: 0;
  max-height: 120px;
  overflow-y: auto;
}

.subagent-view__unsent-item {
  display: flex;
  align-items: flex-start;
  gap: 6px;
  padding: 2px 0;
}

.subagent-view__unsent-text {
  flex: 1;
  min-width: 0;
  overflow-wrap: anywhere;
  white-space: pre-wrap;
  user-select: text;
}

.subagent-view__unsent-item button {
  border: 1px solid #d6b85a;
  border-radius: 5px;
  background: #fff;
  color: #5f4b00;
  padding: 2px 8px;
  cursor: pointer;
}

.subagent-view__unsent-item button:disabled {
  opacity: 0.5;
  cursor: default;
}

.subagent-view__transcript {
  flex: 1;
  min-height: 0;
  display: flex;
  flex-direction: column;
}
</style>
