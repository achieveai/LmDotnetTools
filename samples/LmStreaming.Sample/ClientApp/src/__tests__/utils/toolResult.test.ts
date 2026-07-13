import { describe, it, expect } from 'vitest';
import { unwrapResult, parseExitCode, isErrorResult } from '@/utils/toolResult';

// Real persisted fixtures (shape: { family, toolName, functionArgs, result, isError, ... }).
import weatherDouble from '../fixtures/persisted/weather.doubleenc.json';
import calcDouble from '../fixtures/persisted/calculate.doubleenc.json';
import taskOutput from '../fixtures/persisted/taskoutput.obj.json';
import updateTaskStr from '../fixtures/persisted/updatetask.str.json';
import bashPlain from '../fixtures/persisted/bash.plain.json';
import shellExit from '../fixtures/persisted/shell.exitcode.json';
import agentOverLimit from '../fixtures/persisted/agent.overlimit.json';
import mcpError from '../fixtures/persisted/mcperror.silentfail.json';
import globPaths from '../fixtures/persisted/glob.paths.json';

// Fixtures typed loosely — value shapes vary per tool family.
const asRecord = (v: unknown) => v as Record<string, unknown>;

describe('unwrapResult', () => {
  describe('real persisted fixtures', () => {
    it('sees through a double-encoded weather object', () => {
      const { value, text } = unwrapResult(weatherDouble.result);
      expect(typeof value).toBe('object');
      expect(asRecord(value).location).toBe('New York');
      expect(asRecord(value).temperature).toBe(74);
      // text is the inner JSON-looking string (the last string before the terminal object).
      expect(text).toContain('"location"');
    });

    it('sees through a double-encoded calculate object', () => {
      const { value } = unwrapResult(calcDouble.result);
      expect(typeof value).toBe('object');
      expect(asRecord(value).result).toBe(4);
      expect(asRecord(value).expression).toBe('2 add 2');
    });

    it('parses a single-encoded object result and keeps raw text', () => {
      const { value, text } = unwrapResult(taskOutput.result);
      expect(typeof value).toBe('object');
      expect(asRecord(value).exit_code).toBe(0);
      expect(asRecord(value).status).toBe('completed');
      // text is the raw single-encoded object string, unchanged.
      expect(text).toBe(taskOutput.result);
    });

    it('unwraps a single-encoded quoted string to the plain string', () => {
      const { value, text } = unwrapResult(updateTaskStr.result);
      expect(value).toBe("Updated task 1 status to 'in progress'.");
      expect(text).toBe("Updated task 1 status to 'in progress'.");
    });

    it('leaves plain (non-JSON) stdout unchanged', () => {
      const { value, text } = unwrapResult(bashPlain.result);
      expect(value).toBe(bashPlain.result);
      expect(text).toBe(bashPlain.result);
    });

    it('preserves a trailing [Exit code: N] marker verbatim', () => {
      const { value, text } = unwrapResult(shellExit.result);
      expect(value).toBe(shellExit.result);
      expect(text).toBe(shellExit.result);
      expect(text).toContain('[Exit code: 22]');
    });
  });

  describe('encoding edge cases', () => {
    it('round-trips a synthetic double-encoded object', () => {
      const obj = { a: 1, nested: { b: 'x' }, arr: [1, 2, 3] };
      const doubleEnc = JSON.stringify(JSON.stringify(obj));
      const { value } = unwrapResult(doubleEnc);
      expect(value).toEqual(obj);
    });

    it('does NOT mangle a large integer string (text stays verbatim)', () => {
      const big = '12345678901234567890';
      const { text } = unwrapResult(big);
      expect(text).toBe(big); // verbatim, not the lossy Number
    });

    it('treats a bare JSON number string as a terminal non-string', () => {
      const { value, text } = unwrapResult('42');
      expect(value).toBe(42);
      expect(text).toBe('42');
    });

    it('unwraps a top-level quoted string one layer', () => {
      const { value, text } = unwrapResult('"hello"');
      expect(value).toBe('hello');
      expect(text).toBe('hello');
    });
  });

  describe('nullish and empty inputs', () => {
    const cases: Array<[string, string | null | undefined]> = [
      ['null', null],
      ['undefined', undefined],
      ['empty string', ''],
    ];
    for (const [label, input] of cases) {
      it(`returns { value: '', text: '' } for ${label}`, () => {
        expect(unwrapResult(input)).toEqual({ value: '', text: '' });
      });
    }
  });
});

describe('parseExitCode', () => {
  const cases: Array<[string, string | null | undefined, number | null]> = [
    ['trailing exit code 22', 'curl failed\n\n[Exit code: 22]', 22],
    ['exit code 0', '[Exit code: 0]', 0],
    ['no code present', 'no code here', null],
    ['non-trailing exit code', '[Exit code: 5] then more text', null],
    ['null input', null, null],
    ['undefined input', undefined, null],
  ];
  for (const [label, input, expected] of cases) {
    it(`returns ${expected} for ${label}`, () => {
      expect(parseExitCode(input)).toBe(expected);
    });
  }

  it('reads the real shell.exitcode fixture as 22', () => {
    expect(parseExitCode(shellExit.result)).toBe(22);
  });
});

describe('isErrorResult', () => {
  describe('real persisted fixtures', () => {
    it('is true via the is_error flag (agent over-limit)', () => {
      expect(isErrorResult(agentOverLimit.result, agentOverLimit.isError)).toBe(true);
    });

    it('is true via the error key even when the flag is false (agent over-limit)', () => {
      // Proves the error-key path independent of the flag.
      expect(isErrorResult(agentOverLimit.result, false)).toBe(true);
    });

    it('is true for an MCP infra error persisted with is_error=false', () => {
      expect(isErrorResult(mcpError.result, false)).toBe(true);
    });

    it('is true for a non-zero shell exit persisted with is_error=false', () => {
      expect(isErrorResult(shellExit.result, false)).toBe(true);
    });

    it('is false for a plain successful bash result', () => {
      expect(isErrorResult(bashPlain.result, bashPlain.isError)).toBe(false);
    });

    it('is false for a glob path list (no error)', () => {
      expect(isErrorResult(globPaths.result, globPaths.isError)).toBe(false);
    });

    it('is false for a structured result with exit_code 0', () => {
      expect(isErrorResult(taskOutput.result, taskOutput.isError)).toBe(false);
    });
  });

  describe('synthetic cases', () => {
    it('is false for a neutral "0 matches found" string', () => {
      expect(isErrorResult('0 matches found', false)).toBe(false);
    });

    it('is false for a zero exit code marker', () => {
      expect(isErrorResult('done\n\n[Exit code: 0]', false)).toBe(false);
    });

    it('is true for a truthy error key in a double-encoded object', () => {
      const enc = JSON.stringify(JSON.stringify({ error: 'boom' }));
      expect(isErrorResult(enc, false)).toBe(true);
    });

    it('is false for an object with a falsy error key', () => {
      const enc = JSON.stringify({ error: '', ok: true });
      expect(isErrorResult(enc, false)).toBe(false);
    });

    it('is true purely from the flag with no payload', () => {
      expect(isErrorResult(null, true)).toBe(true);
    });

    it('is false for nullish input and no flag', () => {
      expect(isErrorResult(null)).toBe(false);
      expect(isErrorResult(undefined)).toBe(false);
      expect(isErrorResult('')).toBe(false);
    });
  });
});
