<script setup lang="ts">
import { computed } from 'vue';
import type { DisplayItem } from '@/types';
import { useToolResult } from '@/composables/useToolResult';
import { deriveToolPillState } from '@/utils/toolPillState';
import { findPendingQuestions } from '@/utils/pendingQuestions';
import QuestionRich from './tools/QuestionRich.vue';

/**
 * Docks any question the run is currently blocked on directly above the chat input.
 *
 * Answering an `AskUserQuestion` is a capability the CLIENT exposes to the server — it is not a
 * property of the agent or sub-agent that happened to ask. So the form lives where the user
 * answers (next to the text box), while the tool call stays in the transcript as history. Inside
 * the metadata pill the form was reachable only after expanding a collapsed body, and then sat in
 * a 150px scroll box that auto-scrolls away from it as the run produces more items.
 *
 * Renders nothing at all when nothing is pending, so it costs no vertical space in the common case.
 *
 * WHY THIS IS PER-VIEW, NOT ONE GLOBAL SURFACE: a sub-agent's answer must travel over that
 * child's own `/ws/subagent` connection — the root socket does not know a descendant's
 * `toolCallId` and replies `not_found` — and that connection exists only while the child is
 * focused. Mounting the dock inside each view makes it inherit that view's own
 * `GET_RESULT_FOR_TOOL_CALL` and `SUBMIT_CLIENT_TOOL_RESULT` providers (SubAgentTranscript shadows
 * both), so routing falls out of placement with no extra plumbing. A single global dock would
 * render sub-agent questions on the main tab as forms that cannot be submitted.
 */
const props = defineProps<{ displayItems: DisplayItem[] }>();

const { getResultForToolCall } = useToolResult();

const pending = computed(() => findPendingQuestions(props.displayItems, getResultForToolCall));

/**
 * `QuestionRich` needs the same `ToolPillView` the pill builds. `deriveToolPillState` is pure and
 * mount-free, so it is built here from the same inputs `ToolPill` uses — one derivation rule, two
 * call sites, no divergence.
 */
const cards = computed(() =>
  pending.value.map((q) => ({
    id: q.id,
    toolCall: q.toolCall,
    view: deriveToolPillState({
      functionArgs: q.toolCall.function_args,
      result: q.result.result ?? null,
      hasResult: true,
      isErrorFlag: q.result.is_error ?? null,
      isDeferred: true,
    }),
  }))
);
</script>

<template>
  <div v-if="cards.length" class="question-dock" data-testid="question-dock">
    <div v-for="card in cards" :key="card.id" class="question-dock__card">
      <div class="question-dock__header">
        <span class="question-dock__icon" aria-hidden="true">❓</span>
        <span>Waiting for your answer</span>
      </div>
      <QuestionRich :view="card.view" :tool-call="card.toolCall" />
    </div>
  </div>
</template>

<style scoped>
.question-dock {
  display: flex;
  flex-direction: column;
  gap: 8px;
  padding: 0 16px 8px;
  /* Capped on the DOCK, not on each card: multiple pending questions are supported, so a per-card
     cap would let N cards stack to N x 45vh, collapse the message list and let the surrounding
     overflow-hidden layout clip the input itself. Sized generously so the common 1-2 question case
     never scrolls at all -- the whole point of this component is that the form is not hidden. */
  max-height: 45vh;
  overflow-y: auto;
}

.question-dock__card {
  border: 1px solid #f0c36d;
  border-left: 3px solid #e8a33d;
  border-radius: 8px;
  background: #fffdf7;
  padding: 12px 14px;
  /* Cards must not shrink when the dock scrolls, or a long card would compress instead of the
     dock scrolling past it. */
  flex-shrink: 0;
}

.question-dock__header {
  display: flex;
  align-items: center;
  gap: 6px;
  margin-bottom: 8px;
  font-size: 12px;
  font-weight: 600;
  color: #8a5a00;
}

.question-dock__icon {
  font-size: 13px;
}
</style>
