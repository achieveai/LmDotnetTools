import { describe, expect, it } from 'vitest';
import { mount } from '@vue/test-utils';
import ToolCheckboxList from '@/components/ToolCheckboxList.vue';
import type { ToolDefinition } from '@/types/chatMode';

const catalog: ToolDefinition[] = [
  { name: 'web_search', id: 'web_search', group: 'builtin', groupLabel: 'Built-in (server-side)' },
  { name: 'calculate', id: 'calculate', group: 'sample', groupLabel: 'Sample tools' },
  {
    name: 'All workspace tools',
    id: 'sandbox:*',
    group: 'sandbox',
    groupLabel: 'Workspace (sandbox)',
    isWildcard: true,
    requiresSandbox: true,
  },
  {
    name: 'Bash',
    id: 'sandbox:Bash',
    description: 'Run a shell command.',
    group: 'sandbox',
    groupLabel: 'Workspace (sandbox)',
    requiresSandbox: true,
  },
  {
    name: 'Read',
    id: 'sandbox:Read',
    description: 'Read a file.',
    group: 'sandbox',
    groupLabel: 'Workspace (sandbox)',
    requiresSandbox: true,
  },
];

function mountList(modelValue: string[] = []) {
  return mount(ToolCheckboxList, { props: { tools: catalog, modelValue } });
}

function lastEmitted(wrapper: ReturnType<typeof mountList>): string[] {
  const events = wrapper.emitted('update:modelValue');
  expect(events).toBeTruthy();
  return events![events!.length - 1][0] as string[];
}

describe('ToolCheckboxList', () => {
  it('renders one section per group with its heading', () => {
    const wrapper = mountList();

    expect(wrapper.find('[data-testid="tool-group-builtin"]').exists()).toBe(true);
    expect(wrapper.find('[data-testid="tool-group-sample"]').exists()).toBe(true);
    const sandbox = wrapper.find('[data-testid="tool-group-sandbox"]');
    expect(sandbox.exists()).toBe(true);
    expect(sandbox.text()).toContain('Workspace (sandbox)');
  });

  it('shows the qualified tools that used to be missing entirely', () => {
    // The reported symptom: none of these rows existed in the editor at all.
    const wrapper = mountList();

    expect(wrapper.find('[data-testid="tool-sandbox:Bash"]').exists()).toBe(true);
    expect(wrapper.find('[data-testid="tool-sandbox:*"]').exists()).toBe(true);
  });

  it('writes the group wildcard when a qualified group header is ticked', async () => {
    // Not every current tool id: only the wildcard keeps covering tools a plugin adds later.
    const wrapper = mountList();

    await wrapper.find('[data-testid="tool-group-toggle-sandbox"]').setValue(true);

    expect(lastEmitted(wrapper)).toEqual(['sandbox:*']);
  });

  it('writes individual ids when an unqualified group header is ticked', () => {
    const wrapper = mountList();

    wrapper.find('[data-testid="tool-group-toggle-sample"]').setValue(true);

    expect(lastEmitted(wrapper)).toEqual(['calculate']);
  });

  it('renders rows covered by the wildcard as checked and not togglable', () => {
    const wrapper = mountList(['sandbox:*']);

    const bash = wrapper.find('[data-testid="tool-sandbox:Bash"]');
    expect((bash.element as HTMLInputElement).checked).toBe(true);
    expect(bash.attributes('disabled')).toBeDefined();
  });

  it('counts a wildcard-selected group as fully selected', () => {
    const wrapper = mountList(['sandbox:*']);

    expect(wrapper.find('[data-testid="tool-group-collapse-sandbox"]').text()).toContain('2/2');
  });

  it('adds and removes an individual tool', async () => {
    const wrapper = mountList(['sandbox:Bash']);

    await wrapper.find('[data-testid="tool-sandbox:Read"]').setValue(true);
    expect(lastEmitted(wrapper)).toEqual(['sandbox:Bash', 'sandbox:Read']);

    await wrapper.setProps({ modelValue: ['sandbox:Bash', 'sandbox:Read'] });
    await wrapper.find('[data-testid="tool-sandbox:Bash"]').setValue(false);
    expect(lastEmitted(wrapper)).toEqual(['sandbox:Read']);
  });

  it('emits in catalog order regardless of click order', async () => {
    const wrapper = mountList(['sandbox:Read']);

    await wrapper.find('[data-testid="tool-calculate"]').setValue(true);

    expect(lastEmitted(wrapper)).toEqual(['calculate', 'sandbox:Read']);
  });

  it('searches across every group, not just the first', async () => {
    const wrapper = mountList();

    await wrapper.find('[data-testid="tools-search"]').setValue('shell command');

    expect(wrapper.find('[data-testid="tool-sandbox:Bash"]').exists()).toBe(true);
    expect(wrapper.find('[data-testid="tool-group-sample"]').exists()).toBe(false);
  });

  it('matches on the qualified id as well as the display name', async () => {
    const wrapper = mountList();

    await wrapper.find('[data-testid="tools-search"]').setValue('sandbox:read');

    expect(wrapper.find('[data-testid="tool-sandbox:Read"]').exists()).toBe(true);
  });

  it('counts tools rather than tokens in the summary', () => {
    // The wildcard is a selection token, not a tool; counting it would overstate the surface.
    const wrapper = mountList(['sandbox:*']);

    expect(wrapper.find('[data-testid="tools-selection-summary"]').text()).toContain('2 of 4');
  });

  it('says the summary is open-ended when a wildcard is selected', () => {
    const wrapper = mountList(['sandbox:*']);

    expect(wrapper.find('[data-testid="tools-selection-summary"]').text()).toContain(
      'all current and future tools in Workspace (sandbox)'
    );
  });

  it('reports no tools enabled when nothing is ticked', () => {
    const wrapper = mountList();

    expect(wrapper.find('[data-testid="tools-selection-summary"]').text()).toContain(
      'No tools enabled'
    );
  });

  it('warns about the per-conversation sandbox cost only once a workspace tool is selected', async () => {
    const wrapper = mountList(['calculate']);
    expect(wrapper.find('[data-testid="tools-sandbox-note"]').exists()).toBe(false);

    await wrapper.setProps({ modelValue: ['sandbox:Read'] });
    expect(wrapper.find('[data-testid="tools-sandbox-note"]').text()).toContain(
      'sandbox session'
    );
  });

  it('surfaces a catalog warning on the group it belongs to', () => {
    const wrapper = mount(ToolCheckboxList, {
      props: {
        tools: catalog.map((t) =>
          t.group === 'sandbox' ? { ...t, catalogWarning: 'gateway unreachable' } : t
        ),
        modelValue: [],
      },
    });

    expect(wrapper.find('[data-testid="tool-group-warning-sandbox"]').text()).toContain(
      'gateway unreachable'
    );
    expect(wrapper.find('[data-testid="tool-group-warning-sample"]').exists()).toBe(false);
  });

  it('collapses and expands a group', async () => {
    const wrapper = mountList();
    const collapse = wrapper.find('[data-testid="tool-group-collapse-sandbox"]');

    expect(collapse.attributes('aria-expanded')).toBe('true');
    await collapse.trigger('click');
    expect(
      wrapper.find('[data-testid="tool-group-collapse-sandbox"]').attributes('aria-expanded')
    ).toBe('false');
  });

  it('re-expands a collapsed group while searching so a match is never hidden', async () => {
    const wrapper = mountList();
    await wrapper.find('[data-testid="tool-group-collapse-sandbox"]').trigger('click');

    await wrapper.find('[data-testid="tools-search"]').setValue('Bash');

    expect(
      wrapper.find('[data-testid="tool-group-collapse-sandbox"]').attributes('aria-expanded')
    ).toBe('true');
  });

  it('select all takes each qualified group by wildcard', async () => {
    const wrapper = mountList();

    await wrapper.find('[data-testid="tools-select-all"]').trigger('click');

    expect(lastEmitted(wrapper)).toEqual(['web_search', 'calculate', 'sandbox:*']);
  });

  it('deselect all clears everything', async () => {
    const wrapper = mountList(['sandbox:*', 'calculate']);

    await wrapper.find('[data-testid="tools-deselect-all"]').trigger('click');

    expect(lastEmitted(wrapper)).toEqual([]);
  });
});
