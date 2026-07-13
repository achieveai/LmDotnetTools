import { describe, it, expect } from 'vitest';
import {
  parseDiff,
  parseMatches,
  parseTerminal,
  parseCodeBlock,
} from '@/utils/toolParsers';

import editFx from '../fixtures/persisted/edit.diff.json';
import grepFx from '../fixtures/persisted/grep.matches.json';
import shellFx from '../fixtures/persisted/shell.exitcode.json';
import taskOutputFx from '../fixtures/persisted/taskoutput.obj.json';
import bashFx from '../fixtures/persisted/bash.plain.json';
import readFx from '../fixtures/persisted/read.numbered.json';
import writeFx from '../fixtures/persisted/write.confirm.json';

describe('toolParsers', () => {
  describe('parseDiff', () => {
    it('renders old lines as del rows and new lines as add rows (edit fixture)', () => {
      const args = JSON.parse(editFx.functionArgs) as {
        old_string: string;
        new_string: string;
        replace_all?: boolean;
      };

      const model = parseDiff(args.old_string, args.new_string, args.replace_all);

      expect(model.removed).toBeGreaterThan(0);
      expect(model.added).toBeGreaterThan(0);
      expect(model.removed).toBe(args.old_string.split('\n').length);
      expect(model.added).toBe(args.new_string.split('\n').length);

      const firstDel = model.rows.find(r => r.kind === 'del');
      const firstAdd = model.rows.find(r => r.kind === 'add');
      expect(firstDel?.text).toBe(args.old_string.split('\n')[0]);
      expect(firstAdd?.text).toBe(args.new_string.split('\n')[0]);

      // Del rows precede add rows.
      const firstAddIndex = model.rows.findIndex(r => r.kind === 'add');
      const lastDelIndex =
        model.rows.length - 1 - [...model.rows].reverse().findIndex(r => r.kind === 'del');
      expect(lastDelIndex).toBeLessThan(firstAddIndex);

      // No replace_all in the fixture → false.
      expect(model.replaceAll).toBe(false);
    });

    it('honors the replaceAll flag', () => {
      expect(parseDiff('a', 'b', true).replaceAll).toBe(true);
      expect(parseDiff('a', 'b').replaceAll).toBe(false);
    });

    it('treats nullish and empty strings as zero lines', () => {
      expect(parseDiff(null, null)).toEqual({
        rows: [],
        added: 0,
        removed: 0,
        replaceAll: false,
      });
      expect(parseDiff('', '')).toEqual({
        rows: [],
        added: 0,
        removed: 0,
        replaceAll: false,
      });
      expect(parseDiff(undefined, 'x')).toEqual({
        rows: [{ kind: 'add', text: 'x' }],
        added: 1,
        removed: 0,
        replaceAll: false,
      });
    });

    it('counts multiple lines', () => {
      const model = parseDiff('a\nb', 'c\nd\ne');
      expect(model.removed).toBe(2);
      expect(model.added).toBe(3);
      expect(model.rows).toEqual([
        { kind: 'del', text: 'a' },
        { kind: 'del', text: 'b' },
        { kind: 'add', text: 'c' },
        { kind: 'add', text: 'd' },
        { kind: 'add', text: 'e' },
      ]);
    });
  });

  describe('parseMatches', () => {
    it('parses ripgrep-style grep output (grep fixture)', () => {
      const model = parseMatches(grepFx.result);

      expect(model.groups.length).toBeGreaterThanOrEqual(1);
      expect(model.groups[0].file).toContain('local_backend.rs');

      const anyMatch = model.groups[0].lines.some(l => l.isMatch);
      expect(anyMatch).toBe(true);

      const match263 = model.groups[0].lines.find(l => l.lineNo === 263);
      expect(match263?.isMatch).toBe(true);

      expect(model.totalMatches).toBe(20);
      expect(model.summary).toContain('matches found');
    });

    it('distinguishes context lines from match lines', () => {
      const model = parseMatches(grepFx.result);
      const context = model.groups[0].lines.find(l => !l.isMatch && l.lineNo != null);
      expect(context).toBeDefined();
      expect(context?.isMatch).toBe(false);
    });

    it('returns an empty model for empty / nullish input', () => {
      expect(parseMatches('')).toEqual({ groups: [], summary: '', totalMatches: 0 });
      expect(parseMatches(null)).toEqual({ groups: [], summary: '', totalMatches: 0 });
      expect(parseMatches(undefined)).toEqual({ groups: [], summary: '', totalMatches: 0 });
    });

    it('keeps lines before any file heading in a null-file group', () => {
      const model = parseMatches('12:some match line\nfile.rs:\n5:another');
      expect(model.groups[0].file).toBeNull();
      expect(model.groups[0].lines[0]).toEqual({ lineNo: 12, text: 'some match line', isMatch: true });
      expect(model.groups[1].file).toBe('file.rs');
    });

    it('treats a "0 matches" body as empty groups', () => {
      const model = parseMatches('No matches found');
      expect(model.groups).toEqual([]);
      expect(model.totalMatches).toBe(0);
    });
  });

  describe('parseTerminal', () => {
    it('extracts a trailing [Exit code: N] from a plain string (shell fixture)', () => {
      const model = parseTerminal(shellFx.result);
      expect(model.exitCode).toBe(22);
      expect(model.failed).toBe(true);
      expect(model.stdout).toContain('curl: (22)');
      expect(model.stdout).not.toContain('[Exit code:');
      expect(model.stderr).toBe('');
    });

    it('reads structured TaskOutput objects (taskoutput fixture)', () => {
      const structured = JSON.parse(taskOutputFx.result) as Record<string, unknown>;
      const model = parseTerminal('', { structured });
      expect(model.exitCode).toBe(0);
      expect(model.failed).toBe(false);
      expect(model.stdout).toContain('python3');
    });

    it('leaves exitCode null for plain output with no exit code (bash fixture)', () => {
      const model = parseTerminal(bashFx.result);
      expect(model.exitCode).toBeNull();
      expect(model.failed).toBe(false);
      expect(model.stdout).toBe(bashFx.result);
      expect(model.stderr).toBe('');
    });

    it('marks structured status:"failed" as failed even with exit code 0', () => {
      const model = parseTerminal('', {
        structured: { stdout: 'x', stderr: 'y', exit_code: 0, status: 'failed' },
      });
      expect(model.exitCode).toBe(0);
      expect(model.failed).toBe(true);
      expect(model.stdout).toBe('x');
      expect(model.stderr).toBe('y');
    });

    it('marks a non-zero structured exit_code as failed', () => {
      const model = parseTerminal('', { structured: { exit_code: 2 } });
      expect(model.exitCode).toBe(2);
      expect(model.failed).toBe(true);
      expect(model.stdout).toBe('');
      expect(model.stderr).toBe('');
    });

    it('handles nullish text with fallback exit code', () => {
      expect(parseTerminal(null)).toEqual({
        stdout: '',
        stderr: '',
        exitCode: null,
        failed: false,
      });
      expect(parseTerminal(null, { exitCode: 7 })).toEqual({
        stdout: '',
        stderr: '',
        exitCode: 7,
        failed: true,
      });
    });

    it('uses opts.exitCode for plain text without a trailing marker', () => {
      const model = parseTerminal('hello world', { exitCode: 3 });
      expect(model.exitCode).toBe(3);
      expect(model.failed).toBe(true);
      expect(model.stdout).toBe('hello world');
    });
  });

  describe('parseCodeBlock', () => {
    it('parses numbered Read output (read fixture)', () => {
      const model = parseCodeBlock(null, readFx.result);
      expect(model.mode).toBe('numbered');
      expect(model.lines[0].lineNo).toBe(1);
      expect(model.lines[0].text).toContain('Sandbox Demo');
      expect(model.content).toBe(readFx.result);
    });

    it('uses the Write content arg when present (write fixture)', () => {
      const args = JSON.parse(writeFx.functionArgs) as Record<string, unknown>;
      expect(typeof args.content).toBe('string');

      const model = parseCodeBlock(args, writeFx.result);
      expect(model.mode).toBe('plain');
      expect(model.content).toBe(args.content);
      expect(model.lines).toEqual([]);
    });

    it('falls back to raw text when args carry no content', () => {
      const model = parseCodeBlock({ file_path: '/x' }, 'raw body');
      expect(model.mode).toBe('plain');
      expect(model.content).toBe('raw body');
      expect(model.lines).toEqual([]);
    });

    it('falls back to empty string when nothing is available', () => {
      expect(parseCodeBlock(null, null)).toEqual({ mode: 'plain', lines: [], content: '' });
      expect(parseCodeBlock(null, undefined)).toEqual({ mode: 'plain', lines: [], content: '' });
    });

    it('parses every numbered line with its number stripped', () => {
      const model = parseCodeBlock(null, '     1\tfirst\n     2\tsecond\n');
      expect(model.mode).toBe('numbered');
      expect(model.lines).toEqual([
        { lineNo: 1, text: 'first' },
        { lineNo: 2, text: 'second' },
      ]);
    });
  });
});
