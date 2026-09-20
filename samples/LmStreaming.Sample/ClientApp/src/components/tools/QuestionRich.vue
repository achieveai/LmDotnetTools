<script lang="ts">
interface StoredDraftAnswer {
  selectedValues: string[];
  otherText: string;
  otherActive: boolean;
  skipped: boolean;
  comment: string;
}

interface StoredQuestionDraft {
  argsSignature: string;
  currentIndex: number;
  drafts: Record<string, StoredDraftAnswer>;
}

/** Shared by component instances for this page session only; never persisted to browser storage. */
const questionDraftMemory = new Map<string, StoredQuestionDraft>();
let questionInstanceSequence = 0;
</script>

<script setup lang="ts">
import { computed, reactive, ref, useId, watch } from 'vue';
import type { ToolPillView } from '@/utils/toolTypes';
import type { ToolCall } from '@/types';
import { stripMarkdownPreview } from '@/utils/stripMarkdownPreview';
import { useClientToolSubmit, type ClientToolSubmitOutcome } from '@/composables/useClientToolSubmit';

const props = defineProps<{ view: ToolPillView; toolCall: ToolCall; draftKey?: string }>();
const emit = defineEmits<{ 'busy-change': [busy: boolean] }>();
const inputGroupId = `${useId()}-${questionInstanceSequence++}`;

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
  /**
   * Free commentary the user wrote alongside whatever they chose. Distinct from `otherText`, which
   * belongs to the "Other" OPTION and in single-select replaces the choice — so it could never carry
   * a remark about a choice, only instead of one. Omitted when empty, leaving the payload of an
   * uncommented answer byte-identical to what it has always been. Client-only, like the Cancel body:
   * the server treats `result` as an opaque string and never parses it.
   */
  comment?: string;
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
  comment: string;
}
const drafts = reactive<Record<string, DraftAnswer>>({});
const memoryKey = computed(() => props.draftKey ?? props.toolCall.tool_call_id ?? 'question');
const argsSignature = computed(() => props.toolCall.function_args ?? '');

function replaceDrafts(next: Record<string, DraftAnswer>): void {
  for (const key of Object.keys(drafts)) delete drafts[key];
  for (const [key, value] of Object.entries(next)) {
    drafts[key] = {
      selectedValues: [...value.selectedValues],
      otherText: value.otherText,
      otherActive: value.otherActive,
      skipped: value.skipped,
      comment: value.comment ?? '',
    };
  }
}

watch(
  [memoryKey, argsSignature],
  ([key, signature]) => {
    const stored = questionDraftMemory.get(key);
    if (stored?.argsSignature === signature) {
      currentIndex.value = Math.min(stored.currentIndex, Math.max(questions.value.length - 1, 0));
      replaceDrafts(stored.drafts);
      return;
    }
    questionDraftMemory.delete(key);
    currentIndex.value = 0;
    replaceDrafts({});
  },
  { immediate: true }
);

watch(
  [memoryKey, argsSignature, currentIndex, () => drafts],
  ([key, signature]) => {
    if (!props.view.isDeferred) return;
    questionDraftMemory.set(key as string, {
      argsSignature: signature as string,
      currentIndex: currentIndex.value,
      drafts: Object.fromEntries(
        Object.entries(drafts).map(([id, value]) => [
          id,
          { ...value, selectedValues: [...value.selectedValues] },
        ])
      ),
    });
  },
  { deep: true }
);

watch(
  () => [props.view.isDeferred, props.view.hasResult] as const,
  ([isDeferred, hasResult]) => {
    if (!isDeferred && hasResult) questionDraftMemory.delete(memoryKey.value);
  },
  { immediate: true }
);
function draftFor(idx: number): DraftAnswer {
  const qId = questionIdAt(idx);
  if (!drafts[qId]) {
    drafts[qId] = { selectedValues: [], otherText: '', otherActive: false, skipped: false, comment: '' };
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
  // A comment on its own IS an answer. Without this the user who wants to say something none of the
  // options covers has to pick an option they do not mean, or Skip, to get their words through.
  if (d.comment.trim().length > 0) return true;
  return false;
});

function buildAnswers(): Answer[] {
  return questions.value.map((_, idx) => {
    const qId = questionIdAt(idx);
    const d = draftFor(idx);
    const comment = d.comment.trim();
    return {
      questionId: qId,
      selectedValues: d.skipped ? [] : [...d.selectedValues],
      otherText: !d.skipped && d.otherActive ? d.otherText.trim() : '',
      skipped: d.skipped,
      // Kept even on a Skip: "none of these, because ..." is exactly when commentary matters most.
      ...(comment ? { comment } : {}),
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
watch(isLocked, (busy) => emit('busy-change', busy), { immediate: true });

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
        <div v-if="answerFor(idx)?.comment" class="question__answer-comment">
          {{ answerFor(idx)?.comment }}
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
        <fieldset v-if="idx === currentIndex" class="question__body">
          <legend class="question__prompt">{{ q.prompt }}</legend>
          <p v-if="q.description" class="question__description">{{ q.description }}</p>
          <p class="question__selection-hint">
            {{ q.allowMultiple ? 'Choose any that apply' : 'Choose one' }}
          </p>

          <div class="question__options">
            <label
              v-for="opt in q.options"
              :key="optionValue(opt)"
              class="question__option"
              :class="{ 'question__option--selected': isOptionSelected(idx, optionValue(opt)) }"
              :data-testid="`question-option-${optionValue(opt)}`"
            >
              <input
                :type="q.allowMultiple ? 'checkbox' : 'radio'"
                :name="`question-${inputGroupId}-${questionIdAt(idx)}`"
                :checked="isOptionSelected(idx, optionValue(opt))"
                :disabled="isLocked"
                @change="toggleOption(idx, optionValue(opt))"
              />
              <span class="question__option-copy">
                <span class="question__option-label">{{ opt.label }}</span>
                <span v-if="opt.description" class="question__option-desc">{{ opt.description }}</span>
              </span>
            </label>

            <label
              v-if="q.allowOther"
              class="question__option question__option--other"
              :class="{ 'question__option--selected': draftFor(idx).otherActive }"
              data-testid="question-other-toggle"
            >
              <input
                :type="q.allowMultiple ? 'checkbox' : 'radio'"
                :name="`question-${inputGroupId}-${questionIdAt(idx)}`"
                :checked="draftFor(idx).otherActive"
                :disabled="isLocked"
                @change="toggleOther(idx)"
              />
              <span class="question__option-label">Other</span>
            </label>
            <label v-if="q.allowOther && draftFor(idx).otherActive" class="question__other-field">
              <span>Your answer</span>
              <textarea
                class="question__other-text"
                data-testid="question-other-text"
                rows="3"
                placeholder="Type your answer…"
                :disabled="isLocked"
                v-model="draftFor(idx).otherText"
              />
            </label>
          </div>

          <label class="question__comment-field">
            <span>Comment (optional)</span>
            <textarea
              class="question__comment"
              data-testid="question-comment"
              rows="2"
              placeholder="Anything you want to add alongside your answer…"
              :disabled="isLocked"
              v-model="draftFor(idx).comment"
            />
          </label>

          <p v-if="currentPreview" class="question__preview" data-testid="question-preview">
            {{ currentPreview }}
          </p>
        </fieldset>
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
          class="question__action question__action--quiet"
          data-testid="question-back"
          :disabled="currentIndex === 0 || isLocked"
          @click="goBack"
        >
          Back
        </button>
        <button
          type="button"
          class="question__action question__action--quiet"
          data-testid="question-skip"
          :disabled="isLocked"
          @click="skipCurrent"
        >
          Skip
        </button>
        <button
          type="button"
          class="question__action question__action--cancel"
          data-testid="question-cancel"
          :disabled="isLocked"
          @click="doCancel"
        >
          {{ cancelling ? 'Cancelling…' : 'Cancel request' }}
        </button>
        <button
          v-if="!isLast"
          type="button"
          class="question__action question__action--primary"
          data-testid="question-next"
          :disabled="!canProceed || isLocked"
          @click="goNext"
        >
          Next
        </button>
        <button
          v-else
          type="button"
          class="question__action question__action--primary"
          data-testid="question-submit"
          :disabled="!canProceed || isLocked"
          @click="goNext"
        >
          {{ submitting ? 'Sending…' : 'Send answer' }}
        </button>
      </div>
    </div>
  </div>
</template>

<style scoped>
.question {
  color: #343a40;
  font-size: 14px;
}
.question__context {
  margin: 0 0 16px;
  padding: 10px 12px;
  border-left: 2px solid #cbd1d8;
  border-radius: 0 6px 6px 0;
  background: #f6f7f8;
  color: #5f6874;
  line-height: 1.45;
}
.question__stepper {
  margin-bottom: 8px;
  color: #78818d;
  font-size: 12px;
}
.question__prompt {
  width: 100%;
  padding: 0;
  color: #252a31;
  font-size: 17px;
  font-weight: 600;
  line-height: 1.35;
}
.question__description {
  margin: 6px 0 0;
  color: #5f6874;
  line-height: 1.45;
}
.question__body {
  min-width: 0;
  margin: 0;
  padding: 0;
  border: 0;
}
.question__selection-hint {
  margin: 6px 0 12px;
  color: #78818d;
  font-size: 12px;
}
.question__options {
  display: flex;
  flex-direction: column;
  gap: 8px;
  margin-bottom: 12px;
}
.question__option {
  display: grid;
  grid-template-columns: 18px minmax(0, 1fr);
  align-items: start;
  gap: 10px;
  padding: 10px 12px;
  border: 1px solid #d8dde3;
  border-radius: 7px;
  background: #fff;
  cursor: pointer;
}
.question__option:hover {
  border-color: #aeb7c2;
  background: #fafbfc;
}
.question__option:focus-within {
  outline: 2px solid #2d6cdf;
  outline-offset: 1px;
}
.question__option--selected {
  border-color: #9aabc0;
  background: #f1f5f9;
}
.question__option input {
  margin: 3px 0 0;
  accent-color: #52677f;
}
.question__option-copy {
  display: flex;
  min-width: 0;
  flex-direction: column;
  gap: 2px;
}
.question__option-label {
  color: #343a40;
  font-weight: 500;
  line-height: 1.35;
}
.question__option-desc {
  color: #69727d;
  font-size: 12px;
  line-height: 1.4;
}
.question__other-field {
  display: flex;
  flex-direction: column;
  gap: 5px;
  padding: 2px 0 0 28px;
  color: #5f6874;
  font-size: 12px;
}
.question__other-text {
  width: 100%;
  min-height: 72px;
  box-sizing: border-box;
  resize: vertical;
  padding: 8px 10px;
  border: 1px solid #cbd1d8;
  border-radius: 6px;
  color: #343a40;
  font: inherit;
}
.question__other-text:focus {
  border-color: #2d6cdf;
  outline: 2px solid rgb(45 108 223 / 18%);
}
.question__comment-field {
  display: flex;
  flex-direction: column;
  gap: 5px;
  margin-top: 10px;
  color: #5f6874;
  font-size: 12px;
}
.question__comment {
  width: 100%;
  min-height: 54px;
  box-sizing: border-box;
  resize: vertical;
  padding: 8px 10px;
  border: 1px solid #cbd1d8;
  border-radius: 6px;
  color: #343a40;
  font: inherit;
}
.question__comment:focus {
  border-color: #2d6cdf;
  outline: 2px solid rgb(45 108 223 / 18%);
}
.question__answer-comment {
  margin-top: 4px;
  color: #5f6874;
  font-size: 12px;
  line-height: 1.5;
}
.question__preview {
  margin: 4px 0 10px;
  padding: 8px 10px;
  border-radius: 6px;
  background: #f6f7f8;
  color: #5f6874;
  font-size: 12px;
}
.question__error {
  margin: 10px 0 0;
  color: #b4232e;
  font-size: 12px;
}
.question__submitted {
  margin: 10px 0 0;
  color: #386641;
  font-size: 12px;
}
.question__nav {
  display: flex;
  flex-wrap: wrap;
  justify-content: flex-end;
  gap: 8px;
  margin-top: 16px;
  padding-top: 12px;
  border-top: 1px solid #e4e7eb;
}
.question__action {
  min-height: 34px;
  padding: 6px 12px;
  border: 1px solid #cbd1d8;
  border-radius: 6px;
  background: #fff;
  color: #4f5965;
  cursor: pointer;
  font: inherit;
}
.question__action:hover:not(:disabled) {
  border-color: #9ea8b4;
  background: #f5f6f7;
}
.question__action:focus-visible {
  outline: 2px solid #2d6cdf;
  outline-offset: 1px;
}
.question__action--cancel {
  margin-right: auto;
  color: #7a3e45;
}
.question__action--primary {
  border-color: #52677f;
  background: #52677f;
  color: #fff;
  font-weight: 500;
}
.question__action--primary:hover:not(:disabled) {
  border-color: #44576d;
  background: #44576d;
}
.question__action:disabled {
  opacity: 0.5;
  cursor: default;
}
.question__resolved-row {
  margin-bottom: 8px;
}
.question__answer {
  color: #5f6874;
}
.question__answer--skipped {
  color: #78818d;
  font-style: italic;
}

@media (max-width: 480px) {
  .question__nav {
    display: grid;
    grid-template-columns: repeat(2, minmax(0, 1fr));
  }
  .question__action,
  .question__action--cancel {
    width: 100%;
    margin-right: 0;
  }
}
</style>
