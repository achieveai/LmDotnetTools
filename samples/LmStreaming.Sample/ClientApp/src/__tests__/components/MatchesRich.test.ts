import { describe, it, expect } from 'vitest';
import { mount } from '@vue/test-utils';
import MatchesRich from '@/components/tools/MatchesRich.vue';
import { deriveToolPillState } from '@/utils/toolPillState';
import type { ToolCall } from '@/types';
import grepFx from '../fixtures/persisted/grep.matches.json';

/** Mount MatchesRich from a raw (functionArgs, result) pair, deriving the view exactly as production does. */
function mountRich(functionArgs: string, result: string, isError = false) {
  const view = deriveToolPillState({ functionArgs, result, hasResult: true, isErrorFlag: isError });
  const toolCall: ToolCall = { tool_call_id: 't', function_name: 'Grep', function_args: functionArgs };
  return mount(MatchesRich, { props: { view, toolCall } });
}

describe('MatchesRich', () => {
  it('renders the real grep fixture grouped by file with a matched-line highlight', () => {
    const view = deriveToolPillState({
      functionArgs: grepFx.functionArgs,
      result: grepFx.result,
      hasResult: true,
      isErrorFlag: grepFx.isError,
    });
    const toolCall: ToolCall = {
      tool_call_id: 't',
      function_name: grepFx.toolName,
      function_args: grepFx.functionArgs,
    };
    const wrapper = mount(MatchesRich, { props: { view, toolCall } });

    // Root container present.
    expect(wrapper.find('.matches').exists()).toBe(true);

    // A file heading contains the fixture's path.
    const fileEls = wrapper.findAll('.match-file');
    expect(fileEls.some((el) => el.text().includes('local_backend.rs'))).toBe(true);

    // At least one matched (`hit`) line row.
    expect(wrapper.findAll('.m.hit').length).toBeGreaterThan(0);

    // Summary carries the "N matches found" line.
    expect(wrapper.find('.matches-summary').text()).toContain('matches found');
  });

  it('wraps literal-pattern occurrences in <mark> (no regex metachars)', () => {
    const wrapper = mountRich(
      JSON.stringify({ pattern: 'HostPath' }),
      'src/f.rs:\n263:        super::SkillPathStyle::HostPath\n1 matches found in 1 files'
    );
    const mark = wrapper.find('mark');
    expect(mark.exists()).toBe(true);
    expect(mark.text()).toBe('HostPath');
  });

  it('does NOT render <mark> when the pattern contains a regex metachar (line highlight only)', () => {
    const wrapper = mountRich(
      JSON.stringify({ pattern: 'Skill.*Path' }),
      'src/f.rs:\n263:        super::SkillPathStyle::HostPath\n1 matches found in 1 files'
    );
    expect(wrapper.find('mark').exists()).toBe(false);
    // The matched line still highlights via the row-level `hit` class.
    expect(wrapper.findAll('.m.hit').length).toBeGreaterThan(0);
  });

  it('renders zero matches without crashing and with no .m.hit', () => {
    const wrapper = mountRich(JSON.stringify({ pattern: 'nothing' }), '0 matches found in 0 files');
    expect(wrapper.find('.matches').exists()).toBe(true);
    expect(wrapper.findAll('.m.hit').length).toBe(0);
  });

  it('escapes match text — a <script> in the line is text, not an element', () => {
    const wrapper = mountRich(
      // Non-literal pattern so the whole line renders as one escaped segment.
      JSON.stringify({ pattern: 'no.match' }),
      'src/f.rs:\n10:  const x = "<script>alert(1)</script>";\n1 matches found in 1 files'
    );
    expect(wrapper.find('script').exists()).toBe(false);
    expect(wrapper.html()).toContain('&lt;script&gt;');
  });
});
