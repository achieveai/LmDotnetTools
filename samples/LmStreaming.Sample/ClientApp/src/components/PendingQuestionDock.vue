<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from "vue";
import type { DisplayItem } from "@/types";
import { useToolResult } from "@/composables/useToolResult";
import { deriveToolPillState } from "@/utils/toolPillState";
import { findPendingQuestions } from "@/utils/pendingQuestions";
import BaseModal from "./BaseModal.vue";
import QuestionRich from "./tools/QuestionRich.vue";

const props = withDefaults(
  defineProps<{
    displayItems: DisplayItem[];
    sourceLabel?: string;
    scopeKey?: string;
    active?: boolean;
    requestedQuestionId?: string | null;
  }>(),
  {
    sourceLabel: "This conversation",
    scopeKey: "current",
    active: true,
    requestedQuestionId: null,
  },
);

const emit = defineEmits<{
  "busy-change": [busy: boolean];
  "open-change": [open: boolean];
  opened: [questionId: string];
}>();

const { getResultForToolCall, isQuestionAnswered } = useToolResult();
const pending = computed(() =>
  findPendingQuestions(props.displayItems, getResultForToolCall, isQuestionAnswered),
);
const cards = computed(() =>
  pending.value.map((question) => ({
    ...question,
    view: deriveToolPillState({
      functionArgs: question.toolCall.function_args,
      result: question.result.result ?? null,
      hasResult: true,
      isErrorFlag: question.result.is_error ?? null,
      isDeferred: true,
    }),
  })),
);

const modalOpen = ref(false);
const selectedQuestionId = ref<string | null>(null);
const busy = ref(false);
const autoOpenedIds = new Set<string>();
let handledRequestedQuestionId: string | null = null;

const selectedIndex = computed(() =>
  cards.value.findIndex((card) => card.id === selectedQuestionId.value),
);
const selectedCard = computed(() =>
  selectedIndex.value >= 0 ? cards.value[selectedIndex.value] : null,
);

function setModalOpen(open: boolean): void {
  if (modalOpen.value === open) return;
  modalOpen.value = open;
  emit("open-change", open);
}

function openQuestion(questionId: string): boolean {
  const card = cards.value.find((item) => item.id === questionId);
  if (
    !card ||
    !props.active ||
    (busy.value && questionId !== selectedQuestionId.value)
  )
    return false;
  const changed = selectedQuestionId.value !== questionId || !modalOpen.value;
  selectedQuestionId.value = questionId;
  autoOpenedIds.add(questionId);
  setModalOpen(true);
  if (changed) emit("opened", questionId);
  return true;
}

defineExpose({ openQuestion });

function hideModal(): void {
  if (busy.value) return;
  setModalOpen(false);
}

function resetBusy(): void {
  if (!busy.value) return;
  busy.value = false;
  emit("busy-change", false);
}

function selectOffset(offset: number): void {
  if (busy.value || cards.value.length < 2) return;
  const current = selectedIndex.value < 0 ? 0 : selectedIndex.value;
  const next = (current + offset + cards.value.length) % cards.value.length;
  openQuestion(cards.value[next].id);
}

function handleBusyChange(value: boolean): void {
  if (busy.value === value) return;
  busy.value = value;
  emit("busy-change", value);
}

function hasUnrelatedDialog(): boolean {
  return document.querySelector('[role="dialog"]') !== null && !modalOpen.value;
}

function tryRequestedQuestion(): boolean {
  const requested = props.requestedQuestionId;
  if (!requested || handledRequestedQuestionId === requested) return false;
  if (!cards.value.some((card) => card.id === requested)) return false;
  handledRequestedQuestionId = requested;
  return openQuestion(requested);
}

watch(
  () => props.requestedQuestionId,
  () => {
    handledRequestedQuestionId = null;
    if (props.active) tryRequestedQuestion();
  },
  { immediate: true },
);

watch(
  () =>
    [
      props.active,
      props.scopeKey,
      cards.value.map((card) => card.id).join("\u0000"),
    ] as const,
  ([active, scopeKey], previous) => {
    if (previous && scopeKey !== previous[1]) {
      selectedQuestionId.value = null;
      autoOpenedIds.clear();
      handledRequestedQuestionId = null;
      resetBusy();
      setModalOpen(false);
    }
    if (!active) {
      hideModal();
      return;
    }

    if (tryRequestedQuestion()) return;

    if (
      selectedQuestionId.value &&
      !cards.value.some((card) => card.id === selectedQuestionId.value)
    ) {
      selectedQuestionId.value = null;
      resetBusy();
      setModalOpen(false);
    }
    if (modalOpen.value) return;

    const unseen = cards.value.find((card) => !autoOpenedIds.has(card.id));
    if (unseen && !hasUnrelatedDialog()) openQuestion(unseen.id);
  },
  { immediate: true },
);

onBeforeUnmount(() => {
  resetBusy();
  if (modalOpen.value) emit("open-change", false);
});
</script>

<template>
  <div v-if="cards.length" class="question-dock" data-testid="question-dock">
    <div class="question-dock__message">
      <svg
        class="question-dock__icon"
        viewBox="0 0 24 24"
        aria-hidden="true"
        focusable="false"
      >
        <circle cx="12" cy="12" r="9" />
        <path d="M9.8 9a2.3 2.3 0 1 1 3.5 2c-.8.5-1.3 1-1.3 2" />
        <path d="M12 16.8h.01" />
      </svg>
      <span class="question-dock__copy">
        <strong>Needs your answer</strong>
        <span class="question-dock__source">{{ sourceLabel }}</span>
      </span>
      <span class="question-dock__count" data-testid="question-count">{{
        cards.length
      }}</span>
    </div>
    <button
      type="button"
      class="question-dock__review"
      data-testid="question-review"
      @click="openQuestion(cards[0].id)"
    >
      Review
    </button>
  </div>

  <BaseModal
    v-if="modalOpen && selectedCard"
    title="Needs your answer"
    data-test-id="question-review-modal"
    @close="hideModal"
  >
    <div class="question-review">
      <div class="question-review__context">
        <span>{{ sourceLabel }}</span>
        <span v-if="cards.length > 1" data-testid="question-queue-position">
          {{ selectedIndex + 1 }} of {{ cards.length }}
        </span>
      </div>

      <QuestionRich
        :key="selectedCard.id"
        :view="selectedCard.view"
        :tool-call="selectedCard.toolCall"
        :draft-key="`${scopeKey}:${selectedCard.id}`"
        @busy-change="handleBusyChange"
      />

      <div class="question-review__footer">
        <div
          v-if="cards.length > 1"
          class="question-review__queue"
          aria-label="Pending questions"
        >
          <button
            type="button"
            data-testid="question-previous-pending"
            :disabled="busy"
            @click="selectOffset(-1)"
          >
            Previous
          </button>
          <button
            type="button"
            data-testid="question-next-pending"
            :disabled="busy"
            @click="selectOffset(1)"
          >
            Next
          </button>
        </div>
        <button
          type="button"
          class="question-review__later"
          data-testid="question-later"
          :disabled="busy"
          @click="hideModal"
        >
          Later
        </button>
      </div>
    </div>
  </BaseModal>
</template>

<style scoped>
.question-dock {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  margin: 0 16px 8px;
  padding: 8px 10px;
  border: 1px solid #d9dee5;
  border-radius: 8px;
  background: #f7f8fa;
  color: #4f5966;
}

.question-dock__message,
.question-dock__copy,
.question-review__context,
.question-review__footer,
.question-review__queue {
  display: flex;
  align-items: center;
}

.question-dock__message {
  min-width: 0;
  gap: 8px;
}

.question-dock__icon {
  width: 16px;
  height: 16px;
  flex: 0 0 auto;
  fill: none;
  stroke: currentColor;
  stroke-width: 1.7;
  stroke-linecap: round;
  stroke-linejoin: round;
}

.question-dock__copy {
  min-width: 0;
  gap: 6px;
  font-size: 13px;
}

.question-dock__source {
  overflow: hidden;
  color: #697482;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.question-dock__count {
  min-width: 20px;
  padding: 1px 6px;
  border-radius: 999px;
  background: #e7eaf0;
  color: #5f6874;
  font-size: 11px;
  text-align: center;
}

.question-dock__review,
.question-review__footer button {
  border: 1px solid #cbd2dc;
  border-radius: 6px;
  background: #fff;
  color: #465160;
  font: inherit;
  cursor: pointer;
}

.question-dock__review {
  flex: 0 0 auto;
  padding: 5px 10px;
}

.question-review {
  min-width: 0;
  padding: 16px 20px 20px;
}

.question-review__context {
  justify-content: space-between;
  gap: 12px;
  margin-bottom: 12px;
  color: #697482;
  font-size: 12px;
}

.question-review__footer {
  justify-content: space-between;
  gap: 12px;
  margin-top: 16px;
}

.question-review__queue {
  gap: 6px;
}

.question-review__footer button {
  padding: 6px 10px;
}

.question-review__footer button:disabled {
  cursor: default;
  opacity: 0.55;
}

.question-review__later {
  margin-left: auto;
}

@media (max-width: 520px) {
  .question-dock {
    margin-inline: 10px;
  }

  .question-dock__copy {
    align-items: flex-start;
    flex-direction: column;
    gap: 1px;
  }

  .question-review {
    padding: 14px 14px 16px;
  }
}
</style>
