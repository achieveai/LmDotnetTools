// gen-md-showcase-prompt.mjs — regenerates the "Markdown Rendering Showcase" prompt for
// PromptExamples.md from the markdown source below, so the escaped one-line JSON in the docs
// can never drift out of sync by hand-editing.
//
//   node playwright-scripts/gen-md-showcase-prompt.mjs
//
// Keep MARKDOWN identical to the copy in markdown-render-audit.mjs.
import { writeFileSync } from 'node:fs';

export const MARKDOWN = [
  '# Streaming Architecture Review',
  '',
  'The pipeline is **healthy** overall, with _two_ follow-ups worth tracking. Latency is measured at the `MessageTransformationMiddleware` boundary.',
  '',
  '## Findings',
  '',
  '### 1. Merge keys collide across turns',
  '',
  'Turn 2+ reuses the run-scoped `generationId`, so `(generationId, messageOrderIdx)` is not unique.',
  '',
  '> Any fix that changes message identity must ship a multi-turn end-to-end test.',
  '> Single-turn green is not evidence.',
  '',
  '### 2. Tool pills duplicate on resume',
  '',
  'Steps to reproduce:',
  '',
  '1. Start a tool-heavy run',
  '2. Switch to another conversation mid-run',
  '3. Switch back before the run completes',
  '   - the pill count freezes',
  '   - a full page reload "fixes" it',
  '',
  '## Latency by stage',
  '',
  '| Stage | p50 (ms) | p99 (ms) | Budget |',
  '| --- | ---: | ---: | :---: |',
  '| Provider stream | 42 | 180 | ok |',
  '| Transform | 3 | 11 | ok |',
  '| WebSocket fan-out | 8 | 260 | over |',
  '',
  '## Suggested patch',
  '',
  '```csharp',
  'if (msg.RunId is null && currentRunId is not null)',
  '{',
  '    msg = msg with { RunId = currentRunId };',
  '}',
  '```',
  '',
  'And on the client:',
  '',
  '```ts',
  "const key = [kind, runId, generationId, orderIdx].join('-');",
  '```',
  '',
  '## Checklist',
  '',
  '- [x] Reproduce with 12 tool calls',
  '- [ ] Add `useChatResume` coverage',
  '- [ ] Backfill persisted conversations',
  '',
  '---',
  '',
  'See the [pipeline notes](https://example.com/pipeline) for the full trace. Terms: HTTP/2, SSE.',
].join('\n');

export const PROMPT =
  '<|instruction_start|>' +
  JSON.stringify({
    instruction_chain: [
      { id: 'md-showcase', id_message: 'Markdown showcase', messages: [{ text: MARKDOWN }] },
    ],
  }) +
  '<|instruction_end|>';

if (process.argv[2]) {
  writeFileSync(process.argv[2], PROMPT);
} else {
  process.stdout.write(PROMPT + '\n');
}
