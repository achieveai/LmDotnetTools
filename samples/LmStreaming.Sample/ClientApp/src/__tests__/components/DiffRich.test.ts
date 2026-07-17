import { describe, it, expect } from 'vitest';
import { mount } from '@vue/test-utils';
import DiffRich from '@/components/tools/DiffRich.vue';
import { deriveToolPillState } from '@/utils/toolPillState';
import { parseDiff } from '@/utils/toolParsers';
import type { ToolCall } from '@/types';
import editFx from '../fixtures/persisted/edit.diff.json';

/** Build the (view, toolCall) prop pair the component expects from a raw args string. */
function mountFromArgs(functionArgs: string, result = '', hasResult = false) {
  const view = deriveToolPillState({ functionArgs, result, hasResult, isErrorFlag: false });
  const toolCall: ToolCall = {
    tool_call_id: 't',
    function_name: editFx.toolName,
    function_args: functionArgs,
  };
  return mount(DiffRich, { props: { view, toolCall } });
}

describe('DiffRich — real edit fixture', () => {
  const view = deriveToolPillState({
    functionArgs: editFx.functionArgs,
    result: editFx.result,
    hasResult: true,
    isErrorFlag: editFx.isError,
  });
  const toolCall: ToolCall = {
    tool_call_id: 't',
    function_name: editFx.toolName,
    function_args: editFx.functionArgs,
  };

  const args = JSON.parse(editFx.functionArgs) as { old_string: string; new_string: string };
  const expected = parseDiff(args.old_string, args.new_string);

  it('renders the diff root with at least one del row and one add row', () => {
    const w = mount(DiffRich, { props: { view, toolCall } });
    expect(w.find('.diff').exists()).toBe(true);
    expect(w.findAll('.diff-row.del').length).toBeGreaterThanOrEqual(1);
    expect(w.findAll('.diff-row.add').length).toBeGreaterThanOrEqual(1);
  });

  it('shows +added / −removed counts matching parseDiff of the same args (both > 0)', () => {
    const w = mount(DiffRich, { props: { view, toolCall } });
    expect(expected.added).toBeGreaterThan(0);
    expect(expected.removed).toBeGreaterThan(0);
    // Independently: counts equal the line counts of new_string / old_string.
    expect(expected.added).toBe(args.new_string.split('\n').length);
    expect(expected.removed).toBe(args.old_string.split('\n').length);

    const stat = w.get('.diff-stat').text();
    expect(stat).toContain(`+${expected.added}`);
    expect(stat).toContain(`−${expected.removed}`);
  });

  it('first del row == first line of old_string; first add row == first line of new_string (asserted independently — they share a first line)', () => {
    const w = mount(DiffRich, { props: { view, toolCall } });
    const firstDel = w.findAll('.diff-row.del')[0].get('.line').text();
    const firstAdd = w.findAll('.diff-row.add')[0].get('.line').text();
    expect(firstDel).toBe(args.old_string.split('\n')[0]);
    expect(firstAdd).toBe(args.new_string.split('\n')[0]);
  });

  it('does not render the replace-all marker when replace_all is absent', () => {
    const w = mount(DiffRich, { props: { view, toolCall } });
    expect(w.get('.diff-stat').text()).not.toContain('replace all');
  });

  it('shows the file path header from args.file_path', () => {
    const w = mount(DiffRich, { props: { view, toolCall } });
    const fp = (JSON.parse(editFx.functionArgs) as { file_path: string }).file_path;
    expect(w.get('.diff-path').text()).toBe(fp);
  });
});

describe('DiffRich — escaping & replace-all', () => {
  it('renders new_string HTML as escaped text (no real <script> element)', () => {
    const evil = '<script>alert(1)</script>';
    const w = mountFromArgs(JSON.stringify({ old_string: 'safe', new_string: evil }));
    // The payload is present as TEXT, never parsed into a live element.
    expect(w.find('script').exists()).toBe(false);
    expect(w.findAll('.diff-row.add')[0].get('.line').text()).toBe(evil);
  });

  it('shows the "replace all" marker when replace_all is true', () => {
    const w = mountFromArgs(JSON.stringify({ old_string: 'a', new_string: 'b', replace_all: true }));
    expect(w.get('.diff-stat').text()).toContain('replace all');
  });
});
