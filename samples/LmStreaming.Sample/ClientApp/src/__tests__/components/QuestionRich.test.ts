import { describe, it, expect, vi } from 'vitest';
import { mount } from '@vue/test-utils';
import QuestionRich from '@/components/tools/QuestionRich.vue';
import { deriveToolPillState } from '@/utils/toolPillState';
import { SUBMIT_CLIENT_TOOL_RESULT } from '@/composables/useClientToolSubmit';
import type { ClientToolSubmitFn, ClientToolSubmitOutcome } from '@/composables/useClientToolSubmit';
import type { ToolCall } from '@/types';

/** Build the (view, toolCall) prop pair against the locked #246 AskUserQuestion schema. */
function mountQuestion(
  functionArgs: string,
  opts: {
    result?: string;
    hasResult?: boolean;
    isDeferred?: boolean;
    isErrorFlag?: boolean;
    toolCallId?: string;
    submit?: ClientToolSubmitFn;
  } = {}
) {
  const {
    result = '',
    hasResult = false,
    isDeferred = false,
    isErrorFlag = false,
    toolCallId = 'q1',
    submit = vi.fn(async () => ({ status: 'acked', duplicate: false }) as ClientToolSubmitOutcome),
  } = opts;
  const view = deriveToolPillState({ functionArgs, result, hasResult, isErrorFlag, isDeferred });
  const toolCall: ToolCall = { tool_call_id: toolCallId, function_name: 'AskUserQuestion', function_args: functionArgs };
  const w = mount(QuestionRich, {
    props: { view, toolCall },
    global: { provide: { [SUBMIT_CLIENT_TOOL_RESULT]: submit } },
  });
  return { w, submit };
}

const singleArgs = JSON.stringify({
  context: 'Need a decision',
  questions: [
    {
      prompt: 'Pick a color',
      options: [
        { label: 'Red', preview: '**Warm** and bold' },
        { label: 'Blue', value: 'blue-val' },
      ],
    },
  ],
});

describe('QuestionRich — awaiting-input, single question, single-select', () => {
  it('renders the context and prompt, submit disabled until an option is chosen', async () => {
    const { w } = mountQuestion(singleArgs, { isDeferred: true });
    expect(w.text()).toContain('Need a decision');
    expect(w.text()).toContain('Pick a color');
    const submitBtn = w.get('[data-testid="question-submit"]');
    expect((submitBtn.element as HTMLButtonElement).disabled).toBe(true);
  });

  it('choosing an option enables submit and shows the safe stripped preview for that option', async () => {
    const { w } = mountQuestion(singleArgs, { isDeferred: true });
    await w.get('[data-testid="question-option-Red"] input').setValue(true);
    const submitBtn = w.get('[data-testid="question-submit"]');
    expect((submitBtn.element as HTMLButtonElement).disabled).toBe(false);
    expect(w.get('[data-testid="question-preview"]').text()).toBe('Warm and bold');
  });

  it('selecting a second option (radio) replaces the first — preview updates, no accumulation', async () => {
    const { w } = mountQuestion(singleArgs, { isDeferred: true });
    await w.get('[data-testid="question-option-Red"] input').setValue(true);
    await w.get('[data-testid="question-option-blue-val"] input').setValue(true);
    expect(w.find('[data-testid="question-preview"]').exists()).toBe(false); // Blue has no preview text
  });

  it('submits { answers: [{ questionId: "q0", selectedValues: ["blue-val"], otherText: "", skipped: false }] }', async () => {
    const submit = vi.fn(async () => ({ status: 'acked', duplicate: false }) as ClientToolSubmitOutcome);
    const { w } = mountQuestion(singleArgs, { isDeferred: true, toolCallId: 'call-9', submit });
    await w.get('[data-testid="question-option-blue-val"] input').setValue(true);
    await w.get('[data-testid="question-submit"]').trigger('click');
    await Promise.resolve();
    await Promise.resolve();

    expect(submit).toHaveBeenCalledTimes(1);
    const [toolCallId, payload, isError] = submit.mock.calls[0];
    expect(toolCallId).toBe('call-9');
    expect(isError).toBe(false);
    expect(JSON.parse(payload)).toEqual({
      answers: [{ questionId: 'q0', selectedValues: ['blue-val'], otherText: '', skipped: false }],
    });
  });

  it('shows a "submitted, waiting" message and disables inputs once acked', async () => {
    const { w } = mountQuestion(singleArgs, { isDeferred: true });
    await w.get('[data-testid="question-option-Red"] input').setValue(true);
    await w.get('[data-testid="question-submit"]').trigger('click');
    await Promise.resolve();
    await Promise.resolve();
    expect(w.get('[data-testid="question-submitted"]').text()).toMatch(/waiting/i);
    expect((w.get('[data-testid="question-option-Red"] input').element as HTMLInputElement).disabled).toBe(true);
  });
});

describe('QuestionRich — Other and Skip', () => {
  const otherArgs = JSON.stringify({
    context: 'ctx',
    questions: [{ prompt: 'Anything else?', allowOther: true, options: [{ label: 'Nothing' }] }],
  });

  it('typing Other text (without selecting a normal option) enables submit', async () => {
    const { w } = mountQuestion(otherArgs, { isDeferred: true });
    expect((w.get('[data-testid="question-submit"]').element as HTMLButtonElement).disabled).toBe(true);
    await w.get('[data-testid="question-other-toggle"] input').setValue(true);
    await w.get('[data-testid="question-other-text"]').setValue('Something specific');
    expect((w.get('[data-testid="question-submit"]').element as HTMLButtonElement).disabled).toBe(false);
  });

  it('submits otherText distinct from selectedValues (never injected into selectedValues)', async () => {
    const submit = vi.fn(async () => ({ status: 'acked', duplicate: false }) as ClientToolSubmitOutcome);
    const { w } = mountQuestion(otherArgs, { isDeferred: true, submit });
    await w.get('[data-testid="question-other-toggle"] input').setValue(true);
    await w.get('[data-testid="question-other-text"]').setValue('Something specific');
    await w.get('[data-testid="question-submit"]').trigger('click');
    await Promise.resolve();
    await Promise.resolve();
    const payload = JSON.parse(submit.mock.calls[0][1]);
    expect(payload.answers[0].selectedValues).toEqual([]);
    expect(payload.answers[0].otherText).toBe('Something specific');
  });

  it('explicit Skip submits { selectedValues: [], otherText: "", skipped: true } for a single question', async () => {
    const submit = vi.fn(async () => ({ status: 'acked', duplicate: false }) as ClientToolSubmitOutcome);
    const { w } = mountQuestion(singleArgs, { isDeferred: true, submit });
    await w.get('[data-testid="question-skip"]').trigger('click');
    await Promise.resolve();
    await Promise.resolve();
    const [, payloadRaw, isError] = submit.mock.calls[0];
    const payload = JSON.parse(payloadRaw);
    expect(payload.answers).toEqual([{ questionId: 'q0', selectedValues: [], otherText: '', skipped: true }]);
    // #246 fix: Skip is semantically distinct from Cancel — it is a normal (non-error) answer.
    expect(isError).toBe(false);
  });
});

// #246 spec-defect fix: the Stop button is unavailable while parked on a pending client-tool
// question (hasPendingClientQuestion deliberately doesn't set isLoading/isSending), and
// disconnect-only isn't real cancellation. QuestionRich exposes an explicit Cancel action that
// sends a structured, self-describing client_tool_result error payload — the server treats
// `result` as an opaque string (ChatWebSocketManager/MultiTurnAgentLoop never parse it), so the
// { error, cancelled: true } shape is a client-only convention agreed with server-gap-tests, not a
// server contract change. Cancel is deliberately distinct from Skip: Skip answers with
// isError:false (a normal, "no preference" answer); Cancel answers with isError:true and a
// `cancelled` marker so a resolved-but-non-answer-shaped result is never mistaken for real answers.
describe('QuestionRich — Cancel (explicit pending-question cancellation)', () => {
  it('renders a Cancel action distinct from Skip', () => {
    const { w } = mountQuestion(singleArgs, { isDeferred: true });
    const cancelBtn = w.get('[data-testid="question-cancel"]');
    const skipBtn = w.get('[data-testid="question-skip"]');
    expect(cancelBtn.element).not.toBe(skipBtn.element);
    expect(cancelBtn.text()).toMatch(/cancel/i);
  });

  it('clicking Cancel sends isError:true with a structured { error, cancelled: true } payload for this tool_call_id', async () => {
    const submit = vi.fn(async () => ({ status: 'acked', duplicate: false }) as ClientToolSubmitOutcome);
    const { w } = mountQuestion(singleArgs, { isDeferred: true, toolCallId: 'call-cancel-1', submit });
    await w.get('[data-testid="question-cancel"]').trigger('click');
    await Promise.resolve();
    await Promise.resolve();

    expect(submit).toHaveBeenCalledTimes(1);
    const [toolCallId, payloadRaw, isError] = submit.mock.calls[0];
    expect(toolCallId).toBe('call-cancel-1');
    expect(isError).toBe(true);
    const payload = JSON.parse(payloadRaw);
    expect(payload.cancelled).toBe(true);
    expect(typeof payload.error).toBe('string');
    expect(payload.error.length).toBeGreaterThan(0);
  });

  it('after Cancel is acked, shows a waiting-for-confirmation state and disables the form — only the server ack/resolution is canonical', async () => {
    const { w } = mountQuestion(singleArgs, { isDeferred: true });
    await w.get('[data-testid="question-option-Red"] input').setValue(true);
    await w.get('[data-testid="question-cancel"]').trigger('click');
    await Promise.resolve();
    await Promise.resolve();

    expect(w.get('[data-testid="question-cancel-pending"]').text()).toMatch(/waiting|confirmation/i);
    expect((w.get('[data-testid="question-option-Red"] input').element as HTMLInputElement).disabled).toBe(true);
    expect((w.get('[data-testid="question-cancel"]').element as HTMLButtonElement).disabled).toBe(true);
  });

  it('a locally-acked Cancel blocks a subsequent Submit — late answers cannot override the cancellation', async () => {
    const submit = vi.fn(async () => ({ status: 'acked', duplicate: false }) as ClientToolSubmitOutcome);
    const { w } = mountQuestion(singleArgs, { isDeferred: true, submit });
    await w.get('[data-testid="question-option-Red"] input').setValue(true);
    await w.get('[data-testid="question-cancel"]').trigger('click');
    await Promise.resolve();
    await Promise.resolve();

    expect((w.get('[data-testid="question-submit"]').element as HTMLButtonElement).disabled).toBe(true);
    expect(submit).toHaveBeenCalledTimes(1); // only the cancel call — no answer ever went out
  });

  it('a terminal cancel-submission error (conflict) shows the message WITHOUT a retry hint', async () => {
    const submit = vi.fn(
      async () => ({ status: 'error', code: 'conflict', message: 'Already answered' }) as ClientToolSubmitOutcome
    );
    const { w } = mountQuestion(singleArgs, { isDeferred: true, submit });
    await w.get('[data-testid="question-cancel"]').trigger('click');
    await Promise.resolve();
    await Promise.resolve();
    const err = w.get('[data-testid="question-cancel-error"]');
    expect(err.text()).toContain('Already answered');
    expect(err.text()).not.toMatch(/try again/i);
  });

  it('a retry-safe cancel-submission error (store_failed) shows a "try again" hint and Cancel stays available', async () => {
    const submit = vi.fn(
      async () => ({ status: 'error', code: 'store_failed', message: 'Could not save' }) as ClientToolSubmitOutcome
    );
    const { w } = mountQuestion(singleArgs, { isDeferred: true, submit });
    await w.get('[data-testid="question-cancel"]').trigger('click');
    await Promise.resolve();
    await Promise.resolve();
    const err = w.get('[data-testid="question-cancel-error"]');
    expect(err.text()).toContain('Could not save');
    expect(err.text()).toMatch(/try again/i);
    expect((w.get('[data-testid="question-cancel"]').element as HTMLButtonElement).disabled).toBe(false);
  });

  it('once the canonical (server-resolved) result is a non-answer body, the interactive form does not reopen', () => {
    const resultText = JSON.stringify({ error: 'Question cancelled by user.', cancelled: true });
    const { w } = mountQuestion(singleArgs, {
      result: resultText,
      hasResult: true,
      isDeferred: false,
      isErrorFlag: true,
    });

    expect(w.find('[data-testid="question-form"]').exists()).toBe(false);
    expect(w.find('[data-testid="question-resolved"]').exists()).toBe(false);
    const resolved = w.get('[data-testid="question-cancelled-resolved"]');
    expect(resolved.text()).toContain('Question cancelled by user.');
  });
});

describe('QuestionRich — multiple choice', () => {
  const multiArgs = JSON.stringify({
    context: 'ctx',
    questions: [
      {
        prompt: 'Pick any',
        allowMultiple: true,
        options: [{ label: 'One' }, { label: 'Two' }, { label: 'Three' }],
      },
    ],
  });

  it('checking multiple options accumulates them all in selectedValues', async () => {
    const submit = vi.fn(async () => ({ status: 'acked', duplicate: false }) as ClientToolSubmitOutcome);
    const { w } = mountQuestion(multiArgs, { isDeferred: true, submit });
    await w.get('[data-testid="question-option-One"] input').setValue(true);
    await w.get('[data-testid="question-option-Three"] input').setValue(true);
    await w.get('[data-testid="question-submit"]').trigger('click');
    await Promise.resolve();
    await Promise.resolve();
    const payload = JSON.parse(submit.mock.calls[0][1]);
    expect(payload.answers[0].selectedValues.sort()).toEqual(['One', 'Three']);
  });

  it('does not show a single-select preview for a multi-select question', async () => {
    const { w } = mountQuestion(multiArgs, { isDeferred: true });
    await w.get('[data-testid="question-option-One"] input').setValue(true);
    expect(w.find('[data-testid="question-preview"]').exists()).toBe(false);
  });
});

describe('QuestionRich — 1-4 question stepper', () => {
  const twoQArgs = JSON.stringify({
    context: 'ctx',
    questions: [
      { prompt: 'First?', options: [{ label: 'A' }] },
      { prompt: 'Second?', options: [{ label: 'B' }] },
    ],
  });

  it('shows "Question 1 of 2", Next disabled until answered, Back disabled on the first question', async () => {
    const { w } = mountQuestion(twoQArgs, { isDeferred: true });
    expect(w.text()).toContain('Question 1 of 2');
    expect((w.get('[data-testid="question-back"]').element as HTMLButtonElement).disabled).toBe(true);
    expect((w.get('[data-testid="question-next"]').element as HTMLButtonElement).disabled).toBe(true);
  });

  it('answering question 1 and clicking Next advances to question 2; Submit appears only on the last', async () => {
    const { w } = mountQuestion(twoQArgs, { isDeferred: true });
    await w.get('[data-testid="question-option-A"] input').setValue(true);
    await w.get('[data-testid="question-next"]').trigger('click');
    expect(w.text()).toContain('Question 2 of 2');
    expect(w.find('[data-testid="question-submit"]').exists()).toBe(true);
    expect(w.find('[data-testid="question-next"]').exists()).toBe(false);
  });

  it('Back returns to question 1 with its prior answer preserved (selecting A again keeps it checked)', async () => {
    const { w } = mountQuestion(twoQArgs, { isDeferred: true });
    await w.get('[data-testid="question-option-A"] input').setValue(true);
    await w.get('[data-testid="question-next"]').trigger('click');
    await w.get('[data-testid="question-back"]').trigger('click');
    expect(w.text()).toContain('Question 1 of 2');
    expect((w.get('[data-testid="question-option-A"] input').element as HTMLInputElement).checked).toBe(true);
  });

  it('submitting on the last question sends BOTH answers in order', async () => {
    const submit = vi.fn(async () => ({ status: 'acked', duplicate: false }) as ClientToolSubmitOutcome);
    const { w } = mountQuestion(twoQArgs, { isDeferred: true, submit });
    await w.get('[data-testid="question-option-A"] input').setValue(true);
    await w.get('[data-testid="question-next"]').trigger('click');
    await w.get('[data-testid="question-option-B"] input').setValue(true);
    await w.get('[data-testid="question-submit"]').trigger('click');
    await Promise.resolve();
    await Promise.resolve();
    const payload = JSON.parse(submit.mock.calls[0][1]);
    expect(payload.answers).toEqual([
      { questionId: 'q0', selectedValues: ['A'], otherText: '', skipped: false },
      { questionId: 'q1', selectedValues: ['B'], otherText: '', skipped: false },
    ]);
  });
});

describe('QuestionRich — submit error handling', () => {
  it('a retry-safe error (store_failed) shows the message with a "try again" hint, form stays enabled', async () => {
    const submit = vi.fn(
      async () => ({ status: 'error', code: 'store_failed', message: 'Could not save' }) as ClientToolSubmitOutcome
    );
    const { w } = mountQuestion(singleArgs, { isDeferred: true, submit });
    await w.get('[data-testid="question-option-Red"] input').setValue(true);
    await w.get('[data-testid="question-submit"]').trigger('click');
    await Promise.resolve();
    await Promise.resolve();
    const err = w.get('[data-testid="question-submit-error"]');
    expect(err.text()).toContain('Could not save');
    expect(err.text()).toMatch(/try again/i);
    expect((w.get('[data-testid="question-submit"]').element as HTMLButtonElement).disabled).toBe(false);
  });

  it('a terminal error (conflict) shows the message WITHOUT a retry hint', async () => {
    const submit = vi.fn(
      async () => ({ status: 'error', code: 'conflict', message: 'Already answered' }) as ClientToolSubmitOutcome
    );
    const { w } = mountQuestion(singleArgs, { isDeferred: true, submit });
    await w.get('[data-testid="question-option-Red"] input').setValue(true);
    await w.get('[data-testid="question-submit"]').trigger('click');
    await Promise.resolve();
    await Promise.resolve();
    const err = w.get('[data-testid="question-submit-error"]');
    expect(err.text()).toContain('Already answered');
    expect(err.text()).not.toMatch(/try again/i);
  });
});

describe('QuestionRich — resolved (read-only canonical result)', () => {
  it('renders the answered label(s) and hides the interactive form once resolved', () => {
    const resultText = JSON.stringify({
      answers: [{ questionId: 'q0', selectedValues: ['blue-val'], otherText: '', skipped: false }],
    });
    const { w } = mountQuestion(singleArgs, { result: resultText, hasResult: true, isDeferred: false });
    expect(w.find('[data-testid="question-form"]').exists()).toBe(false);
    const resolved = w.get('[data-testid="question-resolved"]');
    expect(resolved.text()).toContain('Pick a color');
    expect(resolved.text()).toContain('Blue');
  });

  it('renders "Skipped" for a skipped answer', () => {
    const resultText = JSON.stringify({
      answers: [{ questionId: 'q0', selectedValues: [], otherText: '', skipped: true }],
    });
    const { w } = mountQuestion(singleArgs, { result: resultText, hasResult: true, isDeferred: false });
    expect(w.get('[data-testid="question-resolved"]').text()).toContain('Skipped');
  });
});
