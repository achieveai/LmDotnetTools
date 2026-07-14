import { describe, it, expect } from 'vitest';
import { mount } from '@vue/test-utils';
import fs from 'fs';
import path from 'path';
import MetadataPill from '@/components/MetadataPill.vue';
import { GET_RESULT_FOR_TOOL_CALL } from '@/composables/useToolResult';
import type { ToolsCallMessage, ReasoningMessage, ToolCallResultMessage } from '@/types';
import { MessageType } from '@/types';

function mountPill(
  items: Array<ToolsCallMessage | ReasoningMessage>,
  getResult: (id: string | null | undefined) => ToolCallResultMessage | null = () => null
) {
  return mount(MetadataPill, {
    props: { items },
    global: { provide: { [GET_RESULT_FOR_TOOL_CALL]: getResult } },
  });
}

function toolsCall(id: string, name: string, args: Record<string, unknown>): ToolsCallMessage {
  return {
    $type: MessageType.ToolsCall,
    role: 'assistant',
    tool_calls: [{ tool_call_id: id, function_name: name, function_args: JSON.stringify(args) }],
  };
}

describe('MetadataPill — reasoning (stays inline)', () => {
  it('renders a reasoning item as a thinking-pill with 💭 and a summary', () => {
    const reasoning: ReasoningMessage = {
      $type: MessageType.Reasoning,
      role: 'assistant',
      reasoning: 'I need to fetch the weather for Seattle first',
    };
    const w = mountPill([reasoning]);
    const pill = w.get('[data-testid="thinking-pill"]');
    expect(pill.text()).toContain('💭');
    expect(pill.text()).toContain('Thinking:');
    expect(pill.text()).toContain('I need to fetch the weather');
  });

  it('expands a reasoning item to its reasoning-text on click', async () => {
    const reasoning: ReasoningMessage = {
      $type: MessageType.Reasoning,
      role: 'assistant',
      reasoning: 'step by step plan',
    };
    const w = mountPill([reasoning]);
    await w.get('.pill-item').trigger('click');
    expect(w.get('.reasoning-text').text()).toContain('step by step plan');
  });

  it('renders encrypted reasoning as a placeholder', () => {
    const encrypted: ReasoningMessage = {
      $type: MessageType.Reasoning,
      role: 'assistant',
      reasoning: '',
      visibility: 'Encrypted',
    };
    const w = mountPill([encrypted]);
    expect(w.text()).toContain('Encrypted reasoning');
  });
});

describe('MetadataPill — tool delegation (one pill per tool_call)', () => {
  it('renders a single tool_call as one tool-call-pill carrying the raw data-tool-name', () => {
    const w = mountPill([toolsCall('c1', 'sandbox-Bash', { command: 'ls' })]);
    const pills = w.findAll('[data-testid="tool-call-pill"]');
    expect(pills.length).toBe(1);
    expect(pills[0].attributes('data-tool-name')).toBe('sandbox-Bash');
  });

  it('expands a delegated tool pill to reveal the raw .tool-call-result', async () => {
    const result: ToolCallResultMessage = {
      $type: MessageType.ToolCallResult,
      tool_call_id: 'c2',
      result: 'exact raw output 42',
      role: 'tool',
    };
    const w = mountPill([toolsCall('c2', 'Bash', { command: 'echo' })], (id) =>
      id === 'c2' ? result : null
    );
    await w.get('.tool-pill__header').trigger('click');
    expect(w.get('.tool-call-result').element.textContent).toBe('exact raw output 42');
  });

  it('renders multiple tool_calls in one message as separate pills (no "Tools: N calls" aggregate)', () => {
    const multi: ToolsCallMessage = {
      $type: MessageType.ToolsCall,
      role: 'assistant',
      tool_calls: [
        { tool_call_id: 'a', function_name: 'get_weather', function_args: '{"location":"Seattle"}' },
        { tool_call_id: 'b', function_name: 'get_weather', function_args: '{"location":"NYC"}' },
      ],
    };
    const w = mountPill([multi]);
    expect(w.findAll('[data-testid="tool-call-pill"]').length).toBe(2);
    expect(w.text()).not.toContain('2 calls');
    expect(w.text()).not.toContain('Tools:');
  });

  it('selects the weather renderer (icon) for get_weather while keeping the raw wire name', () => {
    const w = mountPill([toolsCall('wc', 'get_weather', { location: 'Paris' })]);
    const pill = w.get('[data-testid="tool-call-pill"]');
    expect(pill.attributes('data-tool-name')).toBe('get_weather');
    expect(pill.classes()).toContain('f-weather');
    expect(w.get('.tool-pill__icon').text()).toBe('🌤️');
  });
});

describe('MetadataPill — container expansion', () => {
  const many = (n: number) =>
    Array.from({ length: n }, (_, i) => toolsCall(`c${i}`, 'test_function', { index: i }));

  it('shows the "Show all N items" header when there are more than 3 items', () => {
    const w = mountPill(many(5));
    expect(w.find('.pill-header').exists()).toBe(true);
    expect(w.text()).toContain('Show all 5 items');
  });

  it('hides the header when there are 3 or fewer items', () => {
    const w = mountPill(many(3));
    expect(w.find('.pill-header').exists()).toBe(false);
  });

  it('toggles the expanded class on the container', async () => {
    const w = mountPill(many(5));
    const items = w.get('.pill-items');
    expect(items.classes()).not.toContain('expanded');
    await w.get('.pill-header').trigger('click');
    expect(items.classes()).toContain('expanded');
    expect(w.text()).toContain('Collapse');
  });
});

describe('MetadataPill — layout containment', () => {
  const source = fs.readFileSync(
    path.resolve(__dirname, '../../components/MetadataPill.vue'),
    'utf-8'
  );
  it('keeps overflow:hidden on .metadata-pill to contain expanded content', () => {
    expect(source).toMatch(/\.metadata-pill\s*\{[^}]*overflow:\s*hidden/);
  });
});
