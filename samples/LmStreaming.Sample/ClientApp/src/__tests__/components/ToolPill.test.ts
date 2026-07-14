import { describe, it, expect } from 'vitest';
import { mount } from '@vue/test-utils';
import ToolPill from '@/components/ToolPill.vue';
import { GET_RESULT_FOR_TOOL_CALL } from '@/composables/useToolResult';
import { MessageType, type ToolCall, type ToolCallResultMessage } from '@/types';
import fs from 'fs';
import path from 'path';

import bashPlain from '../fixtures/persisted/bash.plain.json';
import readNumbered from '../fixtures/persisted/read.numbered.json';
import agentOverlimit from '../fixtures/persisted/agent.overlimit.json';
import writeConfirm from '../fixtures/persisted/write.confirm.json';
import editDiff from '../fixtures/persisted/edit.diff.json';
import taskoutput from '../fixtures/persisted/taskoutput.obj.json';
import grepMatches from '../fixtures/persisted/grep.matches.json';
import weatherDouble from '../fixtures/persisted/weather.doubleenc.json';
import globPaths from '../fixtures/persisted/glob.paths.json';
import agentSpawned from '../fixtures/persisted/agent.spawned.json';

function resultMsg(id: string, result: string, isError = false): ToolCallResultMessage {
  return { $type: MessageType.ToolCallResult, tool_call_id: id, result, is_error: isError, role: 'tool' };
}

function mountPill(toolCall: ToolCall, result?: ToolCallResultMessage) {
  return mount(ToolPill, {
    props: { toolCall },
    global: {
      provide: {
        [GET_RESULT_FOR_TOOL_CALL]: (id: string | null | undefined) =>
          result && id === toolCall.tool_call_id ? result : null,
      },
    },
  });
}

describe('ToolPill — frozen contract', () => {
  it('carries data-testid=tool-call-pill and the RAW wire name in data-tool-name (sandbox- kept)', () => {
    const tc: ToolCall = { tool_call_id: 'c1', function_name: 'sandbox-Bash', function_args: bashPlain.functionArgs };
    const w = mountPill(tc, resultMsg('c1', bashPlain.result));
    const root = w.get('[data-testid="tool-call-pill"]');
    expect(root.attributes('data-tool-name')).toBe('sandbox-Bash');
  });

  it('preserves case-sensitive wire names (Agent, not agent)', () => {
    const tc: ToolCall = { tool_call_id: 'c2', function_name: 'Agent', function_args: '{"subagent_type":"reviewer"}' };
    const w = mountPill(tc);
    expect(w.get('[data-testid="tool-call-pill"]').attributes('data-tool-name')).toBe('Agent');
  });

  it('omits data-tool-name when the wire name is absent', () => {
    const tc: ToolCall = { tool_call_id: 'c3', function_name: null, function_args: '{}' };
    const w = mountPill(tc);
    expect(w.get('[data-testid="tool-call-pill"]').attributes('data-tool-name')).toBeUndefined();
  });

  it('expanded .tool-call-result innerText equals the EXACT raw result string (not pretty-printed)', async () => {
    const tc: ToolCall = { tool_call_id: 'c4', function_name: 'sandbox-Bash', function_args: bashPlain.functionArgs };
    const w = mountPill(tc, resultMsg('c4', bashPlain.result));
    await w.get('.tool-pill__header').trigger('click');
    const pre = w.get('.tool-call-result');
    // exact raw (untrimmed) — .text() would strip the trailing newline
    expect(pre.element.textContent).toBe(bashPlain.result);
  });

  it('does not render .tool-call-result until there is a result (null tool_call_id → args-only, no crash)', () => {
    const tc: ToolCall = { tool_call_id: null, function_name: 'Read', function_args: '{"file_path":"a.ts"}' };
    const w = mountPill(tc);
    expect(w.find('.tool-call-result').exists()).toBe(false);
    // still resolves a renderer + summary without throwing
    expect(w.get('[data-testid="tool-call-pill"]').classes()).toContain('f-read');
  });
});

describe('ToolPill — expand a11y', () => {
  it('header is a button with aria-expanded that toggles on click', async () => {
    const tc: ToolCall = { tool_call_id: 'e1', function_name: 'Read', function_args: readNumbered.functionArgs };
    const w = mountPill(tc, resultMsg('e1', readNumbered.result));
    const header = w.get('.tool-pill__header');
    expect(header.element.tagName).toBe('BUTTON');
    expect(header.attributes('aria-expanded')).toBe('false');
    expect(w.find('.tool-pill__body').exists()).toBe(false);
    await header.trigger('click');
    expect(header.attributes('aria-expanded')).toBe('true');
    expect(w.find('.tool-pill__body').exists()).toBe(true);
    await header.trigger('click');
    expect(header.attributes('aria-expanded')).toBe('false');
  });
});

describe('ToolPill — states & fallback', () => {
  it('unknown tool falls back to the generic renderer (f-generic, 🔧)', () => {
    const tc: ToolCall = { tool_call_id: 'g1', function_name: 'TotallyUnknownTool', function_args: '{"foo":1}' };
    const w = mountPill(tc);
    const root = w.get('[data-testid="tool-call-pill"]');
    expect(root.classes()).toContain('f-generic');
    expect(w.get('.tool-pill__icon').text()).toBe('🔧');
  });

  it('renders an error state with the message for an error result', async () => {
    const tc: ToolCall = { tool_call_id: 'x1', function_name: 'Agent', function_args: agentOverlimit.functionArgs };
    const w = mountPill(tc, resultMsg('x1', agentOverlimit.result, true));
    expect(w.get('[data-testid="tool-call-pill"]').classes()).toContain('st-error');
    await w.get('.tool-pill__header').trigger('click');
    expect(w.get('.tool-pill__error').text()).toContain('Max concurrent');
  });

  it('shows a background chip when run_in_background is set', () => {
    const tc: ToolCall = { tool_call_id: 'b1', function_name: 'Bash', function_args: '{"command":"sleep 9","run_in_background":true}' };
    const w = mountPill(tc);
    expect(w.get('.tool-pill__chip').text()).toContain('background');
  });

  it('renders the full raw result verbatim for huge output (no content truncation)', async () => {
    const huge = 'X'.repeat(5000);
    const tc: ToolCall = { tool_call_id: 'h1', function_name: 'Bash', function_args: '{"command":"cat big"}' };
    const w = mountPill(tc, resultMsg('h1', huge));
    await w.get('.tool-pill__header').trigger('click');
    expect(w.get('.tool-call-result').text()).toBe(huge);
  });

  it('renders tool result text as escaped text (no v-html)', async () => {
    const evil = '<img src=x onerror=alert(1)> <script>bad()</script>';
    const tc: ToolCall = { tool_call_id: 's1', function_name: 'Bash', function_args: '{"command":"echo"}' };
    const w = mountPill(tc, resultMsg('s1', evil));
    await w.get('.tool-pill__header').trigger('click');
    // present as text, not parsed into elements
    expect(w.get('.tool-call-result').text()).toContain('<img');
    expect(w.find('.tool-call-result img').exists()).toBe(false);
  });
});

describe('ToolPill — live family → rich-component dispatch', () => {
  interface Fx {
    toolName: string;
    functionArgs: string;
    result: string;
    isError: boolean;
  }
  function pillFromFixture(fx: Fx) {
    const tc: ToolCall = { tool_call_id: 'fx', function_name: fx.toolName, function_args: fx.functionArgs };
    return mountPill(tc, resultMsg('fx', fx.result, fx.isError));
  }

  // Each family maps to the rich component's distinctive root class (or null → generic body only).
  const cases: Array<{ name: string; fx: Fx; richRoot: string | null }> = [
    { name: 'read → CodeBlockRich', fx: readNumbered, richRoot: '.code' },
    { name: 'write → CodeBlockRich', fx: writeConfirm, richRoot: '.code' },
    { name: 'edit → DiffRich', fx: editDiff, richRoot: '.diff' },
    { name: 'shell → TerminalRich', fx: bashPlain, richRoot: '.term' },
    { name: 'task → TerminalRich', fx: taskoutput, richRoot: '.term' },
    { name: 'grep → MatchesRich', fx: grepMatches, richRoot: '.matches' },
    { name: 'weather → WeatherRich', fx: weatherDouble, richRoot: '.weather-rich' },
    { name: 'glob → generic (no rich comp)', fx: globPaths, richRoot: null },
    { name: 'agent → generic (no rich comp)', fx: agentSpawned, richRoot: null },
  ];

  for (const c of cases) {
    it(`mounts the right body for ${c.name}`, async () => {
      const w = pillFromFixture(c.fx);
      await w.get('.tool-pill__header').trigger('click');
      if (c.richRoot) {
        expect(w.find(c.richRoot).exists()).toBe(true);
      } else {
        // No rich component for this family — only the generic body + raw result.
        for (const sel of ['.code', '.diff', '.term', '.matches', '.weather-rich']) {
          expect(w.find(sel).exists()).toBe(false);
        }
        expect(w.find('.tool-call-result').exists()).toBe(true);
      }
    });
  }
});

describe('ToolPill — Agent (async sub-agent) family', () => {
  it('collapsed summary shows subagent_type and the prompt', () => {
    const tc: ToolCall = { tool_call_id: 'ag', function_name: 'Agent', function_args: agentSpawned.functionArgs };
    const w = mountPill(tc, resultMsg('ag', agentSpawned.result));
    const summary = w.get('.tool-pill__summary').text();
    const args = JSON.parse(agentSpawned.functionArgs) as { subagent_type: string };
    expect(summary).toContain(args.subagent_type);
    // the prompt (or a prefix of it) is surfaced alongside the type
    expect(summary.length).toBeGreaterThan(args.subagent_type.length);
  });

  it('shows the background chip when the Agent result is {status:"spawned"}', () => {
    const tc: ToolCall = { tool_call_id: 'ag2', function_name: 'Agent', function_args: agentSpawned.functionArgs };
    const w = mountPill(tc, resultMsg('ag2', agentSpawned.result));
    expect(w.find('.tool-pill__chip').exists()).toBe(true);
    expect(w.get('.tool-pill__chip').text()).toContain('background');
  });

  it('renders the spawned agent_id in the raw result on expand', async () => {
    const tc: ToolCall = { tool_call_id: 'ag3', function_name: 'Agent', function_args: agentSpawned.functionArgs };
    const w = mountPill(tc, resultMsg('ag3', agentSpawned.result));
    await w.get('.tool-pill__header').trigger('click');
    expect(w.get('.tool-call-result').element.textContent).toBe(agentSpawned.result);
    expect(w.get('.tool-call-result').text()).toContain('spawned');
  });
});

describe('ToolPill — layout containment guards (re-homed from MetadataPill)', () => {
  const source = fs.readFileSync(
    path.resolve(__dirname, '../../components/ToolPill.vue'),
    'utf-8'
  );
  it('has min-width:0 on .tool-pill to allow flex shrinking', () => {
    expect(source).toMatch(/\.tool-pill\s*\{[^}]*min-width:\s*0/);
  });
  it('has overflow-x:auto on .tool-pill__body for horizontal scroll', () => {
    expect(source).toMatch(/\.tool-pill__body\s*\{[^}]*overflow-x:\s*auto/);
  });
});
