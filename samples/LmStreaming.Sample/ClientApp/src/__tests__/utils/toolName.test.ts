import { describe, it, expect } from 'vitest';
import { resolveRenderer, normalizeToolName, unwrapToolCall } from '@/utils/toolName';
import { deriveToolPillState } from '@/utils/toolPillState';
import { summarizeToolCall } from '@/utils/toolActivity';
import { getRegistry, genericRenderer } from '@/components/tools/registry';
import { MessageType, type ToolCall, type ToolCallResultMessage } from '@/types';
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

describe('unwrapToolCall — an ExecuteTool call renders as the tool it runs', () => {
  /** What a pill derives for a call: its renderer family, parsed args and collapsed summary. */
  function shown(call: ToolCall) {
    const { call: target, via } = unwrapToolCall(call);
    const view = deriveToolPillState({ functionArgs: target.function_args, hasResult: false });
    return {
      name: target.function_name,
      via,
      family: resolveRenderer(target.function_name).family,
      args: view.parsedArgs,
      summary: summarizeToolCall(target.function_name, view),
    };
  }

  const direct: ToolCall = {
    tool_call_id: 'direct',
    function_name: 'Read',
    function_args: '{"file_path":"/workspace/sc2.txt"}',
  };

  it('ExecuteTool(Read) resolves to the Read renderer with the target args and the direct summary', () => {
    const wrapped: ToolCall = {
      tool_call_id: 'wrapped',
      function_name: 'ExecuteTool',
      function_args: '{"tool":"Read","arguments":{"file_path":"/workspace/sc2.txt"}}',
    };

    expect(shown(wrapped)).toEqual({ ...shown(direct), via: 'ExecuteTool' });
    expect(shown(wrapped).family).toBe('read');
    expect(shown(wrapped).args).toEqual({ file_path: '/workspace/sc2.txt' });
    // The result still correlates by the wrapper's id.
    expect(unwrapToolCall(wrapped).call.tool_call_id).toBe('wrapped');
  });

  it('reads a double-encoded `arguments` string once, as the server does', () => {
    const wrapped: ToolCall = {
      tool_call_id: 'strargs',
      function_name: 'ExecuteTool',
      function_args: JSON.stringify({ tool: 'Bash', arguments: JSON.stringify({ command: 'echo sc2' }) }),
    };

    expect(shown(wrapped)).toEqual({
      name: 'Bash',
      via: 'ExecuteTool',
      family: 'shell',
      args: { command: 'echo sc2' },
      summary: 'echo sc2',
    });
  });

  it('treats absent or null `arguments` as no arguments, as the server does', () => {
    for (const args of ['{"tool":"Glob"}', '{"tool":"Glob","arguments":null}']) {
      const unwrapped = unwrapToolCall({ tool_call_id: 'g', function_name: 'ExecuteTool', function_args: args });
      expect(unwrapped.via).toBe('ExecuteTool');
      expect(unwrapped.call.function_name).toBe('Glob');
      expect(unwrapped.call.function_args).toBe('{}');
    }
  });

  // Anything the server would refuse to unwrap keeps today's generic ExecuteTool card.
  const malformed: Array<[string, string | null]> = [
    ['args still streaming', '{"tool":"Re'],
    ['args absent', null],
    ['args not an object', '["Read"]'],
    ['no tool', '{"arguments":{"file_path":"/a"}}'],
    ['tool not a string', '{"tool":5,"arguments":{}}'],
    ['tool blank', '{"tool":"  ","arguments":{}}'],
    ['arguments an array', '{"tool":"Read","arguments":["/a"]}'],
    ['arguments a number', '{"tool":"Read","arguments":5}'],
    ['arguments string not JSON', '{"tool":"Read","arguments":"{not json"}'],
    ['arguments string not an object', '{"tool":"Read","arguments":"[1]"}'],
    ['the target is ExecuteTool itself (nested)', '{"tool":"ExecuteTool","arguments":{"tool":"Bash","arguments":{"command":"ls"}}}'],
  ];

  it.each(malformed)('falls back to the wrapper itself when %s', (_label, functionArgs) => {
    const call: ToolCall = { tool_call_id: 'bad', function_name: 'ExecuteTool', function_args: functionArgs };
    const unwrapped = unwrapToolCall(call);
    expect(unwrapped.call).toBe(call);
    expect(unwrapped.via).toBeNull();
  });

  it('passes a direct call through untouched', () => {
    const unwrapped = unwrapToolCall(direct);
    expect(unwrapped.call).toBe(direct);
    expect(unwrapped.via).toBeNull();
  });
});

describe('unwrapToolCall — the result decides: only a target the server ran renders as that target', () => {
  const call: ToolCall = {
    tool_call_id: 'w',
    function_name: 'ExecuteTool',
    function_args: '{"tool":"Write","arguments":{"file_path":"/a","content":"x"}}',
  };
  const result = (toolName: string | null | undefined): ToolCallResultMessage => ({
    $type: MessageType.ToolCallResult,
    tool_call_id: 'w',
    tool_name: toolName,
    result: '',
    role: 'tool',
  });

  // [label, result, rendered name]
  const cases: Array<[string, ToolCallResultMessage | null, string]> = [
    ['no result yet (still running)', null, 'Write'],
    ['the server ran the target', result('Write'), 'Write'],
    ['the server ran the target (name case differs)', result('write'), 'Write'],
    ['refused or unwrap_failed (result keeps the wrapper name)', result('ExecuteTool'), 'ExecuteTool'],
    ['a result naming some other tool', result('Bash'), 'ExecuteTool'],
    ['a result with no tool name', result(undefined), 'ExecuteTool'],
  ];

  it.each(cases)('%s', (_label, res, renderedName) => {
    const unwrapped = unwrapToolCall(call, res);
    expect(unwrapped.call.function_name).toBe(renderedName);
    expect(unwrapped.via).toBe(renderedName === 'ExecuteTool' ? null : 'ExecuteTool');
  });
});
