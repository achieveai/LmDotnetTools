// ask-question-notify-client.mjs — single-call Playwright smoke check for the browser-hosted
// client tools (#246): AskUserQuestion (QuestionRich) and NotifyClient. Runs the WHOLE flow in
// ONE call and returns structured JSON:
//
//   browser_run_code_unsafe({ filename: "samples/LmStreaming.Sample/playwright-scripts/ask-question-notify-client.mjs" })
//
// Returns { pass, failures, steps }. Assert only DETERMINISTIC, browser-observable state. Uses the
// mock provider `test-anthropic` (wide streaming window) so there are no real LLM calls — both
// tools are registered unconditionally by MultiTurnAgentLoop, so this drives REAL production
// tool-handling code, not a stub.
//
// Scenarios (prompts built programmatically below; see PromptExamples.md → "Manual UI test
// prompts" for the shared conventions):
//   1. AskUserQuestion: answer a single-select question -> resolved view shows the choice -> the
//      parked run resumes with the model's scripted follow-up text.
//   2. AskUserQuestion: Skip -> resolved view shows "Skipped" -> the run resumes.
//   3. AskUserQuestion: reload WHILE pending (via ?threadId=) -> the form is still the interactive,
//      awaiting-answer view (not resolved) -> answering post-reload still resolves + resumes.
//   4. NotifyClient: renders its own notification pill WHILE the run is still streaming (not only
//      after completion), and does not enqueue an extra user turn or fork an extra assistant run.
//   5. NotifyClient: the notification survives a reload (rehydrates from persisted history).
//
// Prereq: app running with the built SPA (non-Development serves wwwroot/dist). Adjust BASE to
// match your instance (see PromptExamples.md's port note — this repo's dev instance answers on
// :5000 by default; other scripts in this folder use other ports for other launch profiles).
async (page) => {
  const BASE = 'http://localhost:5000';
  const PROVIDER = 'test-anthropic';
  const SHOT_DIR = 'B:/sources/LmDotnetTools/.worktrees/WT3/.logs/manual';

  // Build each instruction chain programmatically (JSON.stringify) rather than hand-escaping, per
  // this folder's convention (see subagent-tabs.mjs) — also lets the exact same object literal
  // double as documentation of the AskUserQuestion / NotifyClient argument contracts.
  const askChain = (followUpText) => ({
    instruction_chain: [
      {
        id: 'ask-color',
        id_message: 'Asking a question',
        messages: [
          {
            tool_call: [
              {
                name: 'AskUserQuestion',
                args: {
                  context: 'Need to know your favorite color before continuing.',
                  questions: [
                    {
                      prompt: 'Pick a color',
                      options: [
                        { label: 'Red', value: 'red' },
                        { label: 'Blue', value: 'blue' },
                      ],
                    },
                  ],
                },
              },
            ],
          },
        ],
      },
      { id: 'ask-followup', messages: [{ text: followUpText }] },
    ],
  });
  const notifyChain = {
    instruction_chain: [
      {
        id: 'notify-1',
        messages: [
          {
            tool_call: [
              {
                name: 'NotifyClient',
                args: { message: 'Heads up: kicking off a long summary now.', label: 'Progress' },
              },
            ],
          },
        ],
      },
      { id: 'notify-2', messages: [{ text_message: { length: 300 } }] },
    ],
  };
  const wrap = (chain) => `<|instruction_start|>${JSON.stringify(chain)}<|instruction_end|>`;

  const steps = [];
  const record = (name, pass, detail) => steps.push({ name, pass, detail });
  const tid = (id) => page.locator(`[data-testid="${id}"]`);
  const shot = (name) => page.screenshot({ path: `${SHOT_DIR}/${name}.png` }).catch(() => {});
  const waitIdle = async (timeout = 30000) => {
    await tid('stop-button').waitFor({ state: 'hidden', timeout });
    await tid('send-button').waitFor({ state: 'visible', timeout });
  };
  const send = async (text) => {
    await tid('chat-input-textarea').fill(text);
    await tid('send-button').click();
  };
  const newChat = async () => {
    await page.getByRole('button', { name: '+ New Chat' }).click();
    await tid('provider-selector-button').click();
    await tid(`provider-option-${PROVIDER}`).click();
  };
  const pillByName = (name) => page.locator(`[data-testid="tool-call-pill"][data-tool-name="${name}"]`);
  const waitTextContains = async (locator, needle, timeout = 20000) => {
    const deadline = Date.now() + timeout;
    let text = '';
    while (Date.now() < deadline) {
      text = (await locator.allInnerTexts()).join(' ');
      if (text.includes(needle)) return text;
      await page.waitForTimeout(200);
    }
    return text;
  };
  const currentThreadId = async () => {
    await tid('conversation-item').first().waitFor({ timeout: 10000 });
    return tid('conversation-item').first().getAttribute('data-thread-id');
  };

  try {
    await page.goto(BASE);
    await tid('chat-input-textarea').waitFor({ timeout: 20000 });

    // 1. Answer a single-select question -> resolved view + resumed run.
    await newChat();
    await send(wrap(askChain('Great, blue it is.')));
    await waitIdle();

    await pillByName('AskUserQuestion').waitFor({ timeout: 20000 });
    await pillByName('AskUserQuestion').click(); // rich content only renders once expanded
    await tid('question-form').waitFor({ timeout: 10000 });

    await tid('question-option-blue').click();
    await tid('question-submit').click();

    await tid('question-resolved').waitFor({ timeout: 20000 });
    const resolvedText1 = await tid('question-resolved').innerText();
    record('answer -> resolved view shows Blue', resolvedText1.includes('Blue'), resolvedText1);

    const followUp1 = await waitTextContains(tid('assistant-text'), 'Great, blue it is');
    record('answer -> parked run resumes with scripted follow-up', followUp1.includes('Great, blue it is'), followUp1);
    await waitIdle();
    await shot('01-ask-answered');

    // 2. Skip -> resolved view shows "Skipped" -> run resumes.
    await newChat();
    await send(wrap(askChain('No worries, skipping that then.')));
    await waitIdle();

    await pillByName('AskUserQuestion').waitFor({ timeout: 20000 });
    await pillByName('AskUserQuestion').click();
    await tid('question-form').waitFor({ timeout: 10000 });
    await tid('question-skip').click();

    await tid('question-resolved').waitFor({ timeout: 20000 });
    const resolvedText2 = await tid('question-resolved').innerText();
    record('skip -> resolved view shows Skipped', resolvedText2.includes('Skipped'), resolvedText2);

    const followUp2 = await waitTextContains(tid('assistant-text'), 'skipping that then');
    record('skip -> parked run resumes with scripted follow-up', followUp2.includes('skipping that then'), followUp2);
    await waitIdle();
    await shot('02-ask-skipped');

    // 3. Reload while pending -> still the interactive form (not resolved) -> answer post-reload.
    await newChat();
    await send(wrap(askChain('Thanks, blue noted.')));
    await waitIdle();

    const threadId = await currentThreadId();
    const pillBeforeReload = pillByName('AskUserQuestion');
    await pillBeforeReload.waitFor({ timeout: 20000 });
    await pillBeforeReload.click();
    await tid('question-form').waitFor({ timeout: 10000 });

    await page.goto(`${BASE}/?threadId=${threadId}`);
    await tid('chat-input-textarea').waitFor({ timeout: 20000 });

    const pillAfterReload = pillByName('AskUserQuestion');
    await pillAfterReload.waitFor({ timeout: 20000 });
    await pillAfterReload.click();
    await tid('question-form').waitFor({ timeout: 10000 });
    const resolvedCountAfterReload = await tid('question-resolved').count();
    record('pending question survives reload (still a form, not resolved)', resolvedCountAfterReload === 0, {
      resolvedCountAfterReload,
    });

    await tid('question-option-blue').click();
    await tid('question-submit').click();
    await tid('question-resolved').waitFor({ timeout: 20000 });
    const resolvedText3 = await tid('question-resolved').innerText();
    record('answer after reload -> resolved view shows Blue', resolvedText3.includes('Blue'), resolvedText3);

    const followUp3 = await waitTextContains(tid('assistant-text'), 'blue noted');
    record('answer after reload -> parked run resumes', followUp3.includes('blue noted'), followUp3);
    await waitIdle();
    await shot('03-ask-reload-then-answered');

    // 4. NotifyClient renders LIVE (while the run is still streaming) and does not pause/fork a run.
    await newChat();
    await send(wrap(notifyChain));
    await tid('stop-button').waitFor({ state: 'visible', timeout: 20000 });

    await tid('notification-pill').first().waitFor({ timeout: 20000 });
    const stillStreaming = await tid('stop-button').isVisible();
    record('NotifyClient pill renders WHILE the run is still active', stillStreaming, { stillStreaming });

    const notifyKinds = await tid('notification-pill').evaluateAll((nodes) =>
      nodes.map((n) => n.getAttribute('data-notify-kind') ?? ''));
    record('notification pill has kind client-notification', notifyKinds.includes('client-notification'), notifyKinds);

    await pillByName('NotifyClient').waitFor({ timeout: 20000 });
    await waitIdle(90000);

    const userGroups = await tid('user-message-group').count();
    const assistantGroups = await tid('assistant-message-group').count();
    record('NotifyClient does not enqueue an extra user turn', userGroups === 1, { userGroups });
    record('NotifyClient does not fork an extra assistant run', assistantGroups === 1, { assistantGroups });
    await shot('04-notify-live');

    // 5. NotifyClient notification survives reload (rehydrates from persisted history).
    const notifyThreadId = await currentThreadId();
    await page.goto(`${BASE}/?threadId=${notifyThreadId}`);
    await tid('chat-input-textarea').waitFor({ timeout: 20000 });

    await tid('notification-pill').first().waitFor({ timeout: 20000 });
    const notifyKindsAfterReload = await tid('notification-pill').evaluateAll((nodes) =>
      nodes.map((n) => n.getAttribute('data-notify-kind') ?? ''));
    record(
      'notification survives reload (rehydrated from REST history)',
      notifyKindsAfterReload.includes('client-notification'),
      notifyKindsAfterReload
    );
    const userGroupsAfterReload = await tid('user-message-group').count();
    record('rehydration does not duplicate the notify as a second user bubble', userGroupsAfterReload === 1, {
      userGroupsAfterReload,
    });
    await shot('05-notify-reload');
  } catch (e) {
    record('exception', false, String((e && e.stack) || e));
    await shot('99-error');
  }

  const failures = steps.filter((s) => !s.pass).map((s) => s.name);
  return { pass: failures.length === 0, failures, steps };
}
