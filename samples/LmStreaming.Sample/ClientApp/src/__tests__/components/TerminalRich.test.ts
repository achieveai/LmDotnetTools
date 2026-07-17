import { describe, it, expect } from 'vitest';
import { mount } from '@vue/test-utils';
import TerminalRich from '@/components/tools/TerminalRich.vue';
import { deriveToolPillState } from '@/utils/toolPillState';
import type { ToolPillView } from '@/utils/toolTypes';
import type { ToolCall } from '@/types';

import shellExit from '../fixtures/persisted/shell.exitcode.json'; // Bash, [Exit code: 22], is_error=false
import taskoutput from '../fixtures/persisted/taskoutput.obj.json'; // structured exit_code:0
import bashPlain from '../fixtures/persisted/bash.plain.json'; // no exit marker

/** Minimal shape of the persisted tool fixtures this suite mounts from. */
interface PersistedFixture {
  toolName: string;
  functionArgs: string;
  result: string;
  isError: boolean;
}

function viewFor(fx: PersistedFixture): ToolPillView {
  return deriveToolPillState({
    functionArgs: fx.functionArgs,
    result: fx.result,
    hasResult: true,
    isErrorFlag: fx.isError,
  });
}

function tc(fx: PersistedFixture): ToolCall {
  return { tool_call_id: 't', function_name: fx.toolName, function_args: fx.functionArgs };
}

function mountRich(fx: PersistedFixture) {
  return mount(TerminalRich, { props: { view: viewFor(fx), toolCall: tc(fx) } });
}

describe('TerminalRich — shell Bash results', () => {
  it('shell.exitcode: renders .term, a failed exit line showing 22, stdout without the [Exit code] marker', () => {
    const w = mountRich(shellExit);

    expect(w.find('.term').exists()).toBe(true);

    const exit = w.find('.term-exit');
    expect(exit.exists()).toBe(true);
    expect(exit.classes()).toContain('failed');
    expect(exit.text()).toContain('22');
    // Non-color accessibility glyph accompanies the red .failed class.
    expect(exit.text()).toContain('✗');

    const out = w.get('.term-out').text();
    expect(out).toContain('curl: (22)');
    expect(out).not.toContain('[Exit code:');
  });

  it('bash.plain: no exit marker → no failed exit line, raw stdout preserved verbatim', () => {
    const w = mountRich(bashPlain);

    expect(w.find('.term-exit.failed').exists()).toBe(false);
    // exitCode is null here, so the exit line is omitted entirely (no neutral line either).
    expect(w.find('.term-exit').exists()).toBe(false);

    // Stable snippet from the `ls -la` output.
    const out = w.get('.term-out').text();
    expect(out).toContain('README.md');
    expect(out).toContain('drwxr-xr-x');
    // Exact raw result is preserved (textContent keeps the trailing newline that .text() would trim).
    expect(w.get('.term-out').element.textContent).toBe(bashPlain.result);
  });
});

describe('TerminalRich — structured TaskOutput results', () => {
  it('taskoutput.obj: envelope exit_code 0 but stdout ends with [Exit code: 2] → renders failure (exit 2)', () => {
    const w = mountRich(taskoutput);

    const exit = w.find('.term-exit');
    expect(exit.exists()).toBe(true);
    expect(exit.classes()).toContain('failed');
    expect(exit.text()).toContain('2');
    expect(exit.text()).toContain('✗');

    // stdout is the structured object's `stdout` field, not the raw JSON envelope.
    const out = w.get('.term-out').text();
    expect(out).toContain('python3');
    expect(out).not.toContain('"exit_code"');
  });

  it('a genuinely clean structured result renders a non-failed exit 0 line', () => {
    const clean = JSON.stringify({ exit_code: 0, status: 'completed', stdout: 'all good\n', stderr: '' });
    const view = deriveToolPillState({
      functionArgs: '{}',
      result: clean,
      hasResult: true,
      isErrorFlag: false,
    });
    const w = mount(TerminalRich, {
      props: { view, toolCall: { tool_call_id: 't', function_name: 'TaskOutput', function_args: '{}' } },
    });

    const exit = w.find('.term-exit');
    expect(exit.exists()).toBe(true);
    expect(exit.classes()).not.toContain('failed');
    expect(exit.text()).toContain('exit 0');
    expect(exit.text()).toContain('✓');
  });
});

describe('TerminalRich — escaping', () => {
  it('renders result text as escaped text, never a live <script> element (no v-html)', () => {
    const view: ToolPillView = {
      state: 'success',
      isError: false,
      exitCode: null,
      isBackground: false,
      parsedArgs: null,
      resultText: '<script>alert(1)</script>',
      hasResult: true,
      errorText: null,
    };
    const toolCall: ToolCall = { tool_call_id: 't', function_name: 'sandbox-Bash', function_args: null };

    const w = mount(TerminalRich, { props: { view, toolCall } });

    expect(w.find('script').exists()).toBe(false);
    expect(w.get('.term-out').text()).toContain('<script>');
  });
});
