import { describe, it, expect } from 'vitest';
import { resolveRenderer } from '@/utils/toolName';
import { deriveToolPillState } from '@/utils/toolPillState';

import weatherFx from '../fixtures/persisted/weather.doubleenc.json';
import editFx from '../fixtures/persisted/edit.diff.json';
import grepFx from '../fixtures/persisted/grep.matches.json';
import calcFx from '../fixtures/persisted/calculate.doubleenc.json';

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
