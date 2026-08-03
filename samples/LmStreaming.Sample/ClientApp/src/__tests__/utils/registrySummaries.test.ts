import { describe, it, expect } from 'vitest';
import { resolveRenderer } from '@/utils/toolName';
import { deriveToolPillState } from '@/utils/toolPillState';

import weatherFx from '../fixtures/persisted/weather.doubleenc.json';
import editFx from '../fixtures/persisted/edit.diff.json';
import grepFx from '../fixtures/persisted/grep.matches.json';
import calcFx from '../fixtures/persisted/calculate.doubleenc.json';
import sendLegacyFx from '../fixtures/synthetic/sendmessage.legacy.json';
import sendCollabFx from '../fixtures/synthetic/sendmessage.collab.json';
import checkAgentsFx from '../fixtures/synthetic/checkagents.list.json';

function summarize(wireName: string, fx: { functionArgs: string; result: string; isError: boolean }) {
  const view = deriveToolPillState({
    functionArgs: fx.functionArgs,
    result: fx.result,
    hasResult: true,
    isErrorFlag: fx.isError,
  });
  return resolveRenderer(wireName).summarize(view.parsedArgs, view.resultText, view);
}

describe('registry — enriched collapsed summaries', () => {
  it('weather shows the polished chip (location + temp + condition emoji)', () => {
    const s = summarize('get_weather', weatherFx);
    expect(s).toContain('New York');
    expect(s).toContain('74°F');
    expect(s).toContain('☀️'); // Sunny
  });

  it('weather shows a loading chip before the result arrives', () => {
    const view = deriveToolPillState({ functionArgs: '{"location":"Paris"}', result: null, hasResult: false });
    const s = resolveRenderer('get_weather').summarize(view.parsedArgs, view.resultText, view);
    expect(s).toContain('Paris');
    expect(s).toContain('Loading');
  });

  it('edit shows +added −removed counts', () => {
    const s = summarize('sandbox-Edit', editFx);
    expect(s).toContain('graph_auth.py');
    expect(s).toMatch(/\+\d+ −\d+/);
  });

  it('grep shows the pattern and match count', () => {
    const s = summarize('Grep', grepFx);
    expect(s).toContain('20 matches');
  });

  it('math shows expression = result', () => {
    const s = summarize('calculate', calcFx);
    expect(s).toContain('= 4');
  });

  it('an unknown tool falls back to a generic key:value summary', () => {
    const view = deriveToolPillState({ functionArgs: '{"foo":"bar"}', result: null, hasResult: false });
    const s = resolveRenderer('MysteryTool').summarize(view.parsedArgs, view.resultText, view);
    expect(s).toContain('foo');
    expect(s).toContain('bar');
  });
});

// The collaboration tools (#244) reuse the agent renderer. Both argument vocabularies must render:
// the old one is already on disk in persisted conversations and can never stop working.
describe('registry — collaboration tool summaries (#244)', () => {
  it('summarizes a pre-#244 SendMessage (target + prompt)', () => {
    const s = summarize('SendMessage', sendLegacyFx);
    expect(s).toContain('build-fixer');
    expect(s).toContain('start on task #1');
  });

  it('summarizes a collaboration SendMessage (target + content)', () => {
    const s = summarize('SendMessage', sendCollabFx);
    expect(s).toContain('reviewer');
    expect(s).toContain('Which repo');
  });

  it('summarizes the plural checks, whose agent_ids is a list', () => {
    // A scalar-only lookup returns '' here, leaving the pill blank — the reason firstStringList exists.
    const s = summarize('CheckAgents', checkAgentsFx);
    expect(s).toContain('agent-2');
    expect(s).toContain('agent-3');
  });

  it('routes WaitForAgents and GetAgentTranscript to the same agent renderer', () => {
    expect(resolveRenderer('WaitForAgents').family).toBe('agent');
    expect(resolveRenderer('GetAgentTranscript').family).toBe('agent');
    expect(resolveRenderer('sandbox-CheckAgents').family).toBe('agent');
  });

  it('summarizes GetAgents, which takes no arguments, without throwing', () => {
    const view = deriveToolPillState({ functionArgs: '{}', result: null, hasResult: false });
    const s = resolveRenderer('GetAgents').summarize(view.parsedArgs, view.resultText, view);
    expect(s).toBe('');
  });
});

describe('registry — question (#246 AskUserQuestion)', () => {
  it('resolves via the normalized (lowercase, no sandbox- prefix) wire name', () => {
    expect(resolveRenderer('AskUserQuestion').family).toBe('question');
    expect(resolveRenderer('sandbox-AskUserQuestion').family).toBe('question');
  });

  it('single question: summary shows the prompt, no count prefix', () => {
    const args = JSON.stringify({
      context: 'Need a decision',
      questions: [{ prompt: 'Pick a color', options: [{ label: 'Red' }, { label: 'Blue' }] }],
    });
    const view = deriveToolPillState({ functionArgs: args, result: null, hasResult: false });
    const s = resolveRenderer('AskUserQuestion').summarize(view.parsedArgs, view.resultText, view);
    expect(s).toContain('Pick a color');
    expect(s).not.toMatch(/questions ·/);
  });

  it('multiple questions: summary is prefixed with the question count', () => {
    const args = JSON.stringify({
      context: 'ctx',
      questions: [
        { prompt: 'First?', options: [{ label: 'A' }] },
        { prompt: 'Second?', options: [{ label: 'B' }] },
      ],
    });
    const view = deriveToolPillState({ functionArgs: args, result: null, hasResult: false });
    const s = resolveRenderer('AskUserQuestion').summarize(view.parsedArgs, view.resultText, view);
    expect(s).toContain('2 questions ·');
    expect(s).toContain('First?');
  });

  it('falls back to the shared context when questions is empty/absent', () => {
    const view = deriveToolPillState({
      functionArgs: '{"context":"Just checking in"}',
      result: null,
      hasResult: false,
    });
    const s = resolveRenderer('AskUserQuestion').summarize(view.parsedArgs, view.resultText, view);
    expect(s).toBe('Just checking in');
  });

  it('never throws on missing/malformed args', () => {
    const view = deriveToolPillState({ functionArgs: null, result: null, hasResult: false });
    expect(() => resolveRenderer('AskUserQuestion').summarize(view.parsedArgs, view.resultText, view)).not.toThrow();
  });
});
