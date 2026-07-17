import { describe, it, expect } from 'vitest';
import { deriveToolPillState } from '@/utils/toolPillState';

import weatherDouble from '../fixtures/persisted/weather.doubleenc.json';
import taskoutput from '../fixtures/persisted/taskoutput.obj.json';
import shellExit from '../fixtures/persisted/shell.exitcode.json';
import mcpError from '../fixtures/persisted/mcperror.silentfail.json';
import agentOverlimit from '../fixtures/persisted/agent.overlimit.json';
import agentSpawned from '../fixtures/persisted/agent.spawned.json';
import bashPlain from '../fixtures/persisted/bash.plain.json';
import glob from '../fixtures/persisted/glob.paths.json';

const noResult = (functionArgs: string) => ({ functionArgs, result: null, hasResult: false });
const withResult = (fx: { functionArgs: string; result: string; isError: boolean }) => ({
  functionArgs: fx.functionArgs,
  result: fx.result,
  hasResult: true,
  isErrorFlag: fx.isError,
});

describe('deriveToolPillState', () => {
  describe('state machine', () => {
    it('streaming-args for a growing/partial args prefix with no result', () => {
      const v = deriveToolPillState(noResult('{"location": "Sea'));
      expect(v.state).toBe('streaming-args');
      expect(v.parsedArgs).toBeNull();
      expect(v.hasResult).toBe(false);
    });

    it('awaiting-result once args are valid JSON but no result yet', () => {
      const v = deriveToolPillState(noResult('{"location": "Seattle"}'));
      expect(v.state).toBe('awaiting-result');
      expect(v.parsedArgs).toEqual({ location: 'Seattle' });
    });

    it('empty args with no result → streaming-args (never throws)', () => {
      expect(deriveToolPillState(noResult('')).state).toBe('streaming-args');
      expect(deriveToolPillState({ hasResult: false }).state).toBe('streaming-args');
    });

    it('success when a non-error result is present', () => {
      const v = deriveToolPillState(withResult(bashPlain));
      expect(v.state).toBe('success');
      expect(v.isError).toBe(false);
      expect(v.hasResult).toBe(true);
    });

    it('error when the result is an error', () => {
      const v = deriveToolPillState(withResult(agentOverlimit));
      expect(v.state).toBe('error');
      expect(v.isError).toBe(true);
    });
  });

  describe('error detection (independent of is_error flag)', () => {
    it('MCP-prefix infra error with is_error=false → error', () => {
      const v = deriveToolPillState({ functionArgs: mcpError.functionArgs, result: mcpError.result, hasResult: true, isErrorFlag: false });
      expect(v.state).toBe('error');
      expect(v.errorText).toContain('Error executing MCP tool');
    });

    it('non-zero shell exit with is_error=false → error + exit code surfaced', () => {
      const v = deriveToolPillState({ functionArgs: shellExit.functionArgs, result: shellExit.result, hasResult: true, isErrorFlag: false });
      expect(v.state).toBe('error');
      expect(v.exitCode).toBe(22);
    });

    it('error key surfaces the message', () => {
      const v = deriveToolPillState({ functionArgs: agentOverlimit.functionArgs, result: agentOverlimit.result, hasResult: true, isErrorFlag: false });
      expect(v.isError).toBe(true);
      expect(v.errorText).toContain('Max concurrent');
    });
  });

  describe('exit code', () => {
    it('surfaces a failing captured command even when the TaskOutput envelope reports exit_code 0', () => {
      // taskoutput.obj: envelope exit_code:0/status:"completed", but stdout ends with `[Exit code: 2]`.
      const v = deriveToolPillState(withResult(taskoutput));
      expect(v.exitCode).toBe(2);
      expect(v.isError).toBe(true);
      expect(v.state).toBe('error');
    });

    it('a genuinely clean structured result (exit_code 0, no nested failure) is success', () => {
      const clean = JSON.stringify({ exit_code: 0, status: 'completed', stdout: 'all good\n', stderr: '' });
      const v = deriveToolPillState({ functionArgs: '{}', result: clean, hasResult: true, isErrorFlag: false });
      expect(v.exitCode).toBe(0);
      expect(v.isError).toBe(false);
      expect(v.state).toBe('success');
    });

    it('null when no exit information present', () => {
      expect(deriveToolPillState(withResult(bashPlain)).exitCode).toBeNull();
      expect(deriveToolPillState(withResult(glob)).exitCode).toBeNull();
    });
  });

  describe('structured status', () => {
    it('status:"failed" with exit_code 0 and is_error false → error (consistent with the terminal renderer)', () => {
      const r = JSON.stringify({ exit_code: 0, status: 'failed', stdout: 'boom', stderr: '' });
      const v = deriveToolPillState({ functionArgs: '{}', result: r, hasResult: true, isErrorFlag: false });
      expect(v.isError).toBe(true);
      expect(v.state).toBe('error');
      expect(v.errorText).toBe('Task failed');
    });
  });

  describe('isBackground (static)', () => {
    it('true when run_in_background arg is set', () => {
      const v = deriveToolPillState(noResult('{"command":"sleep 9","run_in_background":true}'));
      expect(v.isBackground).toBe(true);
    });

    it('true when Agent result is {status:"spawned"}', () => {
      const v = deriveToolPillState(withResult(agentSpawned));
      expect(v.isBackground).toBe(true);
    });

    it('false for an ordinary call', () => {
      expect(deriveToolPillState(withResult(bashPlain)).isBackground).toBe(false);
    });
  });

  describe('resultText', () => {
    it('unwraps double-encoded weather to its inner JSON text (verbatim, not mangled)', () => {
      const v = deriveToolPillState(withResult(weatherDouble));
      expect(v.resultText).toContain('"location"');
      expect(v.resultText).toContain('New York');
    });

    it('preserves a plain result string verbatim', () => {
      const v = deriveToolPillState(withResult(bashPlain));
      expect(v.resultText).toBe(bashPlain.result);
    });

    it('is empty when there is no result', () => {
      expect(deriveToolPillState(noResult('{"a":1}')).resultText).toBe('');
    });
  });

  it('never throws on garbage / nullish input', () => {
    expect(() => deriveToolPillState({ functionArgs: '{bad', result: ' ', hasResult: true })).not.toThrow();
    expect(() => deriveToolPillState({ functionArgs: null, result: null, hasResult: false })).not.toThrow();
    expect(() => deriveToolPillState({ hasResult: true, result: undefined })).not.toThrow();
  });
});
