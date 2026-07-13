import { describe, it, expect } from 'vitest';
import { mount } from '@vue/test-utils';
import CodeBlockRich from '@/components/tools/CodeBlockRich.vue';
import { deriveToolPillState } from '@/utils/toolPillState';
import type { ToolPillView } from '@/utils/toolTypes';
import type { ToolCall } from '@/types';
import readFx from '../fixtures/persisted/read.numbered.json';
import writeFx from '../fixtures/persisted/write.confirm.json';

/** Shape of the persisted tool fixtures used here. */
interface Fixture {
  toolName: string;
  toolCallId?: string;
  functionArgs: string;
  result: string;
  isError: boolean;
}

/** Build a real ToolPillView from a fixture via the production state machine. */
function viewFor(fx: Fixture): ToolPillView {
  return deriveToolPillState({
    functionArgs: fx.functionArgs,
    result: fx.result,
    hasResult: true,
    isErrorFlag: fx.isError,
  });
}

function toolCallFor(fx: Fixture): ToolCall {
  return { tool_call_id: fx.toolCallId ?? 't', function_name: fx.toolName, function_args: fx.functionArgs };
}

const anyToolCall: ToolCall = { tool_call_id: 't', function_name: 'Write', function_args: '{}' };

describe('CodeBlockRich — read (numbered)', () => {
  it('renders the root .code container', () => {
    const w = mount(CodeBlockRich, {
      props: { view: viewFor(readFx as Fixture), toolCall: toolCallFor(readFx as Fixture) },
    });
    expect(w.find('.code').exists()).toBe(true);
    expect(w.find('.code.tool-rich').exists()).toBe(true);
  });

  it('renders numbered .code-line rows with a line-number gutter', () => {
    const w = mount(CodeBlockRich, {
      props: { view: viewFor(readFx as Fixture), toolCall: toolCallFor(readFx as Fixture) },
    });
    const rows = w.findAll('.code-line');
    expect(rows.length).toBeGreaterThan(0);

    const first = rows[0];
    expect(first.get('.ln').text()).toBe('1');
    expect(first.get('.src').text()).toContain('Sandbox Demo');
    // numbered mode does not fall back to the plain <pre>
    expect(w.find('.code-plain').exists()).toBe(false);
  });

  it('shows the file_path header from parsedArgs', () => {
    const w = mount(CodeBlockRich, {
      props: { view: viewFor(readFx as Fixture), toolCall: toolCallFor(readFx as Fixture) },
    });
    const header = w.find('.code-path');
    expect(header.exists()).toBe(true);
    expect(header.text()).toContain('README.md');
  });
});

describe('CodeBlockRich — write (plain from args.content)', () => {
  it('renders args.content in a plain <pre>, not the confirmation result', () => {
    const view = viewFor(writeFx as Fixture);
    const w = mount(CodeBlockRich, { props: { view, toolCall: toolCallFor(writeFx as Fixture) } });

    const pre = w.find('.code-plain');
    expect(pre.exists()).toBe(true);

    const content = JSON.parse((writeFx as Fixture).functionArgs).content as string;
    expect(content).toContain('Workspace Memory');
    // the RICH body is the write body (args.content), not the "Successfully wrote…" confirmation
    expect(pre.text()).toContain('Workspace Memory');
    expect(pre.text()).not.toContain('Successfully wrote');
    // plain mode does not emit numbered rows
    expect(w.find('.code-line').exists()).toBe(false);
  });
});

describe('CodeBlockRich — escapes content (no v-html)', () => {
  it('renders plain (args.content) markup as escaped text', () => {
    const view = deriveToolPillState({
      functionArgs: JSON.stringify({
        content: '<script>alert(1)</script><img src=x onerror=bad()>',
        file_path: 'evil.txt',
      }),
      result: 'ok',
      hasResult: true,
    });
    const w = mount(CodeBlockRich, { props: { view, toolCall: anyToolCall } });

    expect(w.find('script').exists()).toBe(false);
    expect(w.find('img').exists()).toBe(false);
    const text = w.get('.code-plain').text();
    expect(text).toContain('<script>');
    expect(text).toContain('<img');
  });

  it('renders numbered (read) source markup as escaped text', () => {
    const view = deriveToolPillState({
      functionArgs: '{}',
      result: '     1\t<script>evil()</script>',
      hasResult: true,
    });
    const w = mount(CodeBlockRich, { props: { view, toolCall: anyToolCall } });

    expect(w.find('script').exists()).toBe(false);
    expect(w.get('.code-line .src').text()).toContain('<script>');
  });
});
