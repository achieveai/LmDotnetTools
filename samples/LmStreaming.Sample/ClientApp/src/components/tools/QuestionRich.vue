<script setup lang="ts">
import { computed, reactive, ref, watch } from 'vue';
import type { ToolPillView } from '@/utils/toolTypes';
import type { ToolCall } from '@/types';
import { stripMarkdownPreview } from '@/utils/stripMarkdownPreview';
import { useClientToolSubmit, type ClientToolSubmitOutcome } from '@/composables/useClientToolSubmit';

const props = defineProps<{ view: ToolPillView; toolCall: ToolCall }>();

// ---------------------------------------------------------------------------
// Wire schema (#246, AskUserQuestion — confirmed with server-track):
// function_args: { context: string, questions: QuestionDef[] } (1-4 entries)
// resolved result: { answers: [{ questionId, selectedValues: string[], otherText, skipped }] }
// ---------------------------------------------------------------------------
interface OptionDef {
  label: string;
  value?: string;
  description?: string;
  preview?: string;
}
interface QuestionDef {
  id?: string;
  prompt: string;
  description?: string;
  allowMultiple?: boolean;
  allowOther?: boolean;
  options: OptionDef[];
}
interface Answer {
  questionId: string;
  selectedValues: string[];
  otherText: string;
  skipped: boolean;
}

function isQuestionDef(v: unknown): v is QuestionDef {
  return !!v && typeof v === 'object' && typeof (v as QuestionDef).prompt === 'string';
}

/** Args parsed defensively — never throws even mid-stream / on a malformed payload. */
const questions = computed<QuestionDef[]>(() => {
  const raw = props.view.parsedArgs?.questions;
  if (!Array.isArray(raw)) return [];
  return raw.filter(isQuestionDef).slice(0, 4);
});
const context = computed<string>(() =>
  typeof props.view.parsedArgs?.context === 'string' ? (props.view.parsedArgs!.context as string) : ''
);

/** questionId per index — mirrors the server's "q0","q1",... default when `id` is omitted. */
function questionIdAt(idx: number): string {
  const q = questions.value[idx];
  return q?.id || `q${idx}`;
}
function optionValue(opt: OptionDef): string {
  return opt.value ?? opt.label;
}

// ---------------------------------------------------------------------------
// Interactive (awaiting-input) form state
// ---------------------------------------------------------------------------
const currentIndex = ref(0);
const isLast = computed(() => currentIndex.value >= questions.value.length - 1);

interface DraftAnswer {
  selectedValues: string[];
  otherText: string;
  otherActive: boolean;
  skipped: boolean;
}
const drafts = reactive<Record<string, DraftAnswer>>({});
function draftFor(idx: number): DraftAnswer {
  const qId = questionIdAt(idx);
  if (!drafts[qId]) {
    drafts[qId] = { selectedValues: [], otherText: '', otherActive: false, skipped: false };
  }
  return drafts[qId];
}

function toggleOption(idx: number, value: string): void {
  const q = questions.value[idx];
  const d = draftFor(idx);
  d.skipped = false;
  if (q.allowMultiple) {
    const at = d.selectedValues.indexOf(value);
    if (at >= 0) d.selectedValues.splice(at, 1);
    else d.selectedValues.push(value);
  } else {
    d.selectedValues = [value];
    d.otherActive = false;
  }
}
function isOptionSelected(idx: number, value: string): boolean {
  return draftFor(idx).selectedValues.includes(value);
}

function toggleOther(idx: number): void {
  const q = questions.value[idx];
  const d = draftFor(idx);
  d.skipped = false;
  d.otherActive = !d.otherActive;
  if (d.otherActive && !q.allowMultiple) {
    d.selectedValues = [];
  }
}

/** Safe single-select preview: the currently selected option's stripped `preview` text. */
const currentPreview = computed<string>(() => {
  const q = questions.value[currentIndex.value];
  if (!q || q.allowMultiple) return '';
  const d = draftFor(currentIndex.value);
  if (d.selectedValues.length !== 1) return '';
  const opt = q.options.find((o) => optionValue(o) === d.selectedValues[0]);
  return opt?.preview ? stripMarkdownPreview(opt.preview) : '';
});

const canProceed = computed<boolean>(() => {
  const d = draftFor(currentIndex.value);
  if (d.skipped) return true;
  if (d.selectedValues.length > 0) return true;
  if (d.otherActive && d.otherText.trim().length > 0) return true;
  return false;
});

function buildAnswers(): Answer[] {
  return questions.value.map((_, idx) => {
    const qId = questionIdAt(idx);
    const d = draftFor(idx);
    return {
      questionId: qId,
      selectedValues: d.skipped ? [] : [...d.selectedValues],
      otherText: !d.skipped && d.otherActive ? d.otherText.trim() : '',
      skipped: d.skipped,
    };
  });
}

// ---------------------------------------------------------------------------
// Submission (#246): sends over the existing socket via the injected submit fn; the resolved
// value itself arrives later as a follow-up ToolCallResultMessage that flips `view.isDeferred`
// false — this component does not optimistically render the resolved state itself.
// ---------------------------------------------------------------------------
const { submit } = useClientToolSubmit();
const submitting = ref(false);
const submitted = ref(false);
const submitError = ref<string | null>(null);
/** conflict / not_found / invalid are terminal — resubmitting cannot help. */
const TERMINAL_ERROR_CODES = new Set(['conflict', 'not_found', 'invalid']);
const submitErrorTerminal = ref(false);

// Cancel (#246 spec-defect fix, declared alongside submit state — see doCancel below for the
// full rationale): local optimistic "cancelling/cancelled" state, separate from submit's.
const cancelling = ref(false);
const cancelled = ref(false);
const cancelError = ref<string | null>(null);
const cancelErrorTerminal = ref(false);

/** Once true, this client instance must never submit anything else — a submit or cancel already went out. */
const isLocked = computed<boolean>(
  () => submitting.value || submitted.value || cancelling.value || cancelled.value
);

async function doSubmit(): Promise<void> {
  if (isLocked.value) return;
  submitting.value = true;
  submitError.value = null;
  submitErrorTerminal.value = false;
  try {
    const toolCallId = props.toolCall.tool_call_id;
    if (!toolCallId) throw new Error('Missing tool_call_id');
    const payload = JSON.stringify({ answers: buildAnswers() });
    const outcome: ClientToolSubmitOutcome = await submit(toolCallId, payload, false);
    if (outcome.status === 'acked') {
      submitted.value = true;
    } else {
      submitError.value = outcome.message;
      submitErrorTerminal.value = TERMINAL_ERROR_CODES.has(outcome.code);
    }
  } catch (err) {
    submitError.value = err instanceof Error ? err.message : 'Failed to submit answer';
    submitErrorTerminal.value = false;
  } finally {
    submitting.value = false;
  }
}

function goNext(): void {
  if (!canProceed.value) return;
  if (isLast.value) {
    void doSubmit();
  } else {
    currentIndex.value += 1;
  }
}
function goBack(): void {
  if (currentIndex.value > 0) currentIndex.value -= 1;
}
function skipCurrent(): void {
  draftFor(currentIndex.value).skipped = true;
  goNext();
}

// #246 spec-defect fix: the Stop button is unavailable while parked on a pending client-tool
// question, and disconnecting isn't real cancellation. This sends the SAME client_tool_result
// frame as a normal answer, but with isError:true and a self-describing { error, cancelled: true }
// body — the server treats `result` as an opaque string (ChatWebSocketManager/MultiTurnAgentLoop
// never parse it), so this is a client-only convention, not a server contract change. Deliberately
// distinct from Skip: Skip answers with isError:false (a normal "no preference" answer); Cancel
// answers with isError:true so a resolved-but-non-answer result can never be mistaken for a real
// answer (see `isResolvedWithoutAnswers` below). Local `cancelling`/`cancelled` only reflect that
// the client's own request went out and was acked by the socket layer — the interactive form only
// actually disappears once the canonical resolved ToolCallResultMessage arrives
// (`props.view.isDeferred` flips false), so a late/racing real answer from this same client can
// never resume or override it (`isLocked` above blocks it at the source).
async function doCancel(): Promise<void> {
  if (isLocked.value) return;
  cancelling.value = true;
  cancelError.value = null;
  cancelErrorTerminal.value = false;
  try {
    const toolCallId = props.toolCall.tool_call_id;
    if (!toolCallId) throw new Error('Missing tool_call_id');
    const payload = JSON.stringify({ error: 'Question cancelled by user.', cancelled: true });
    const outcome: ClientToolSubmitOutcome = await submit(toolCallId, payload, true);
    if (outcome.status === 'acked') {
      cancelled.value = true;
    } else {
      cancelError.value = outcome.message;
      cancelErrorTerminal.value = TERMINAL_ERROR_CODES.has(outcome.code);
    }
  } catch (err) {
    cancelError.value = err instanceof Error ? err.message : 'Failed to cancel';
    cancelErrorTerminal.value = false;
  } finally {
    cancelling.value = false;
  }
}

// Reset the stepper when a genuinely new deferred call mounts on the same pill instance
// (tool_call_id changes) — avoids stale drafts leaking across unrelated questions.
watch(
  () => props.toolCall.tool_call_id,
  () => {
    currentIndex.value = 0;
    submitting.value = false;
    submitted.value = false;
    submitError.value = null;
    submitErrorTerminal.value = false;
    cancelling.value = false;
    cancelled.value = false;
    cancelError.value = null;
    cancelErrorTerminal.value = false;
  }
);

// ---------------------------------------------------------------------------
// Resolved (read-only canonical result)
// ---------------------------------------------------------------------------
const resolvedAnswers = computed<Answer[] | null>(() => {
  if (props.view.isDeferred || !props.view.hasResult) return null;
  try {
    const parsed = JSON.parse(props.view.resultText) as { answers?: Answer[] };
    return Array.isArray(parsed.answers) ? parsed.answers : null;
  } catch {
    return null;
  }
});

// #246 spec-defect fix: a resolved result that is present but NOT `{answers:[...]}`-shaped (e.g.
// this component's own Cancel payload, or any other error the server recorded first) must render
// as a terminal state — never fall through and reopen the interactive form, which would let a
// cancelled/errored question misleadingly look answerable again.
const isResolvedWithoutAnswers = computed<boolean>(
  () => !props.view.isDeferred && props.view.hasResult && resolvedAnswers.value === null
);

function labelsFor(idx: number, values: string[]): string {
  const q = questions.value[idx];
  if (!q) return values.join(', ');
  return values
    .map((v) => q.options.find((o) => optionValue(o) === v)?.label ?? v)
    .join(', ');
}
function answerFor(idx: number): Answer | undefined {
  const qId = questionIdAt(idx);
  return resolvedAnswers.value?.find((a) => a.questionId === qId) ?? resolvedAnswers.value?.[idx];
}
</script>

<template>
  <div class="question tool-rich" data-testid="question-rich">
    <p v-if="context" class="question__context">{{ context }}</p>

    <!-- Resolved: read-only canonical result, one row per question. -->
    <div v-if="resolvedAnswers" class="question__resolved" data-testid="question-resolved">
      <div v-for="(q, idx) in questions" :key="questionIdAt(idx)" class="question__resolved-row">
        <div class="question__prompt">{{ q.prompt }}</div>
        <div v-if="answerFor(idx)?.skipped" class="question__answer question__answer--skipped">Skipped</div>
        <div v-else class="question__answer">
          <span>{{ labelsFor(idx, answerFor(idx)?.selectedValues ?? []) }}</span>
          <span v-if="answerFor(idx)?.otherText"> — {{ answerFor(idx)?.otherText }}</span>
        </div>
      </div>
    </div>

    <!-- Resolved, but NOT answer-shaped (e.g. cancelled): a terminal message, never the form. -->
    <div
      v-else-if="isResolvedWithoutAnswers"
      class="question__resolved question__resolved--cancelled"
      data-testid="question-cancelled-resolved"
    >
      <p class="question__answer question__answer--skipped">
        {{ view.errorText || 'This question was cancelled.' }}
      </p>
    </div>

    <!-- Awaiting input: interactive form, one question at a time. -->
    <div v-else-if="questions.length" class="question__form" data-testid="question-form">
      <div class="question__stepper">Question {{ currentIndex + 1 }} of {{ questions.length }}</div>

      <template v-for="(q, idx) in questions" :key="questionIdAt(idx)">
        <div v-if="idx === currentIndex" class="question__body">
          <p class="question__prompt">{{ q.prompt }}</p>
          <p v-if="q.description" class="question__description">{{ q.description }}</p>

          <div class="question__options">
            <label
              v-for="opt in q.options"
              :key="optionValue(opt)"
              class="question__option"
              :data-testid="`question-option-${optionValue(opt)}`"
            >
              <input
                :type="q.allowMultiple ? 'checkbox' : 'radio'"
                :name="`question-${questionIdAt(idx)}`"
                :checked="isOptionSelected(idx, optionValue(opt))"
                :disabled="isLocked"
                @change="toggleOption(idx, optionValue(opt))"
              />
              <span>{{ opt.label }}</span>
              <span v-if="opt.description" class="question__option-desc">{{ opt.description }}</span>
            </label>

            <label v-if="q.allowOther" class="question__option question__option--other" data-testid="question-other-toggle">
              <input
                :type="q.allowMultiple ? 'checkbox' : 'radio'"
                :name="`question-${questionIdAt(idx)}`"
                :checked="draftFor(idx).otherActive"
                :disabled="isLocked"
                @change="toggleOther(idx)"
              />
              <span>Other</span>
            </label>
            <input
              v-if="q.allowOther && draftFor(idx).otherActive"
              class="question__other-text"
              data-testid="question-other-text"
              type="text"
              placeholder="Type your answer…"
              :disabled="isLocked"
              v-model="draftFor(idx).otherText"
            />
          </div>

          <p v-if="currentPreview" class="question__preview" data-testid="question-preview">
            {{ currentPreview }}
          </p>
        </div>
      </template>

      <p v-if="cancelError" class="question__error" data-testid="question-cancel-error">
        {{ cancelError }}<span v-if="!cancelErrorTerminal"> — you can try again.</span>
      </p>
      <p v-else-if="cancelled" class="question__submitted" data-testid="question-cancel-pending">
        Cancelling… waiting for confirmation…
      </p>
      <p v-else-if="submitError" class="question__error" data-testid="question-submit-error">
        {{ submitError }}<span v-if="!submitErrorTerminal"> — you can try again.</span>
      </p>
      <p v-else-if="submitted" class="question__submitted" data-testid="question-submitted">
        Answer sent — waiting for confirmation…
      </p>

      <div class="question__nav">
        <button
          type="button"
          data-testid="question-back"
          :disabled="currentIndex === 0 || isLocked"
          @click="goBack"
        >
          Back
        </button>
        <button
          type="button"
          data-testid="question-skip"
          :disabled="isLocked"
          @click="skipCurrent"
        >
          Skip
        </button>
        <button
          type="button"
          data-testid="question-cancel"
          :disabled="isLocked"
          @click="doCancel"
        >
          {{ cancelling ? 'Cancelling…' : 'Cancel' }}
        </button>
        <button
          v-if="!isLast"
          type="button"
          data-testid="question-next"
          :disabled="!canProceed || isLocked"
          @click="goNext"
        >
          Next
        </button>
        <button
          v-else
          type="button"
          data-testid="question-submit"
          :disabled="!canProceed || isLocked"
          @click="goNext"
        >
          {{ submitting ? 'Submitting…' : 'Submit' }}
        </button>
      </div>
    </div>
  </div>
</template>

<style scoped>
.question {
  font-size: 13px;
  color: #333;
}
.question__context {
  margin: 0 0 8px;
  color: #555;
  font-style: italic;
}
.question__stepper {
  font-size: 11px;
  color: #888;
  margin-bottom: 6px;
}
.question__prompt {
  font-weight: 600;
  margin: 0 0 4px;
}
.question__description {
  color: #666;
  margin: 0 0 8px;
  font-size: 12px;
}
.question__options {
  display: flex;
  flex-direction: column;
  gap: 6px;
  margin-bottom: 8px;
}
.question__option {
  display: flex;
  align-items: center;
  gap: 6px;
  cursor: pointer;
}
.question__option-desc {
  color: #888;
  font-size: 11px;
}
.question__other-text {
  margin-left: 22px;
  padding: 4px 6px;
  border: 1px solid #d0d0d0;
  border-radius: 4px;
  font: inherit;
}
.question__preview {
  margin: 4px 0 8px;
  padding: 6px 8px;
  background: #f8f9fa;
  border-radius: 4px;
  color: #555;
  font-size: 12px;
}
.question__error {
  color: #d32f2f;
  font-size: 12px;
}
.question__submitted {
  color: #2e7d32;
  font-size: 12px;
}
.question__nav {
  display: flex;
  gap: 8px;
  margin-top: 8px;
}
.question__nav button {
  padding: 4px 12px;
  border: 1px solid #d0d0d0;
  border-radius: 6px;
  background: #fff;
  cursor: pointer;
  font: inherit;
}
.question__nav button:disabled {
  opacity: 0.5;
  cursor: default;
}
.question__resolved-row {
  margin-bottom: 8px;
}
.question__answer--skipped {
  color: #888;
  font-style: italic;
}
</style>
