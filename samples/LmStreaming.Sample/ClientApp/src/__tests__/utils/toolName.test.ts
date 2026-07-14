import { describe, it, expect } from 'vitest';
import { resolveRenderer, normalizeToolName } from '@/utils/toolName';
import { getRegistry, genericRenderer } from '@/components/tools/registry';
import type { ToolFamily, ToolPillView } from '@/utils/toolTypes';

describe('resolveRenderer — name resolution', () => {
  const cases: Array<[string, ToolFamily]> = [
    ['sandbox-Bash', 'shell'],
    ['Bash', 'shell'],
    ['PowerShell', 'shell'],
    ['sandbox-Read', 'read'],
    ['Read', 'read'],
    ['math_eval', 'math'],
    ['calculate', 'math'],
    ['get_weather', 'weather'],
    ['sandbox-Glob', 'glob'],
    ['Grep', 'grep'],
    ['Agent', 'agent'],
    ['CheckAgent', 'agent'],
    ['web_search', 'web'],
    ['Skill', 'skill'],
    ['sandbox-Edit', 'edit'],
    ['Write', 'write'],
    ['TaskOutput', 'task'],
  ];

  it.each(cases)('resolves %s → %s', (wireName, family) => {
    expect(resolveRenderer(wireName).family).toBe(family);
  });
});

describe('resolveRenderer — unknown / empty / nullish → generic', () => {
  it('unknown tool falls back to generic', () => {
    expect(resolveRenderer('TotallyUnknownTool').family).toBe('generic');
  });

  it('empty string falls back to generic', () => {
    expect(resolveRenderer('').family).toBe('generic');
  });

  it('null falls back to generic', () => {
    expect(resolveRenderer(null).family).toBe('generic');
  });

  it('undefined falls back to generic', () => {
    expect(resolveRenderer(undefined).family).toBe('generic');
  });

  it('returns the shared genericRenderer object identity', () => {
    expect(resolveRenderer('TotallyUnknownTool')).toBe(genericRenderer);
  });
});

describe('resolveRenderer — case-insensitive prefix + case-fold', () => {
  it('SANDBOX-bash → shell', () => {
    expect(resolveRenderer('SANDBOX-bash').family).toBe('shell');
  });

  it('sandbox-BASH → shell', () => {
    expect(resolveRenderer('sandbox-BASH').family).toBe('shell');
  });
});

describe('resolveRenderer — shared references (same object identity)', () => {
  it('Bash and sandbox-Bash resolve to the same renderer object', () => {
    expect(resolveRenderer('Bash')).toBe(resolveRenderer('sandbox-Bash'));
  });

  it('edit and multiedit share the same renderer object', () => {
    expect(resolveRenderer('Edit')).toBe(resolveRenderer('MultiEdit'));
  });
});

describe('resolveRenderer — icons', () => {
  it('Read icon is 📄', () => {
    expect(resolveRenderer('Read').icon).toBe('📄');
  });

  it('Grep icon is 🔍', () => {
    expect(resolveRenderer('Grep').icon).toBe('🔍');
  });
});

describe('normalizeToolName', () => {
  it('strips sandbox- prefix and lowercases', () => {
    expect(normalizeToolName('sandbox-Bash')).toBe('bash');
    expect(normalizeToolName('SANDBOX-Read')).toBe('read');
  });

  it('lowercases a bare name', () => {
    expect(normalizeToolName('PowerShell')).toBe('powershell');
  });

  it('returns empty string for nullish/empty input', () => {
    expect(normalizeToolName(null)).toBe('');
    expect(normalizeToolName(undefined)).toBe('');
    expect(normalizeToolName('')).toBe('');
  });
});

describe('registry — data + summarize null-safety', () => {
  const emptyView = {} as ToolPillView;

  it('every registered value is present and shares references across aliases', () => {
    const registry = getRegistry();
    expect(registry.get('bash')).toBe(registry.get('powershell'));
    expect(registry.size).toBeGreaterThan(0);
  });

  it('summarize never throws on null args and returns a string', () => {
    for (const name of ['Read', 'Write', 'Edit', 'Bash', 'Grep', 'Glob', 'Skill', 'Agent', 'math_eval', 'web_search', 'get_weather', 'TaskOutput', 'SetWorkflow', 'Wait', 'TotallyUnknownTool']) {
      const out = resolveRenderer(name).summarize(null, '', emptyView);
      expect(typeof out).toBe('string');
    }
  });

  it('summarize pulls the expected arg for common families', () => {
    expect(resolveRenderer('Read').summarize({ file_path: '/a/b.ts' }, '', emptyView)).toBe('/a/b.ts');
    expect(resolveRenderer('Bash').summarize({ command: 'ls -la' }, '', emptyView)).toBe('ls -la');
    expect(resolveRenderer('math_eval').summarize({ expression: '2+2' }, '', emptyView)).toBe('2+2');
  });

  it('generic summarize joins first args and stays bounded', () => {
    const out = resolveRenderer('TotallyUnknownTool').summarize({ a: 1, b: 'x' }, '', emptyView);
    expect(out).toBe('a: 1, b: x');
    expect(resolveRenderer('TotallyUnknownTool').summarize(null, '', emptyView)).toBe('');
  });
});
