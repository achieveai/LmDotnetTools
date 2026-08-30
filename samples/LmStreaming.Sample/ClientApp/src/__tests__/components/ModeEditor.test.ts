import { describe, expect, it } from 'vitest';
import { mount } from '@vue/test-utils';
import ModeEditor from '@/components/ModeEditor.vue';
import type { ChatMode, ChatModeCreateUpdate, ToolDefinition } from '@/types/chatMode';

const baseMode: ChatMode = {
  id: 'user-1',
  name: 'My Mode',
  description: 'A user mode',
  systemPrompt: 'You are helpful.',
  isSystemDefined: false,
  createdAt: 0,
  updatedAt: 0,
};

function lastSave(wrapper: ReturnType<typeof mount>): ChatModeCreateUpdate {
  const events = wrapper.emitted('save');
  expect(events).toBeTruthy();
  return events![events!.length - 1][0] as ChatModeCreateUpdate;
}

describe('ModeEditor sub-agent prompt fragment', () => {
  it('renders the fragment textarea and placement select, defaulting to append', () => {
    const wrapper = mount(ModeEditor, { props: { mode: null, tools: [] } });

    const textarea = wrapper.get('[data-testid="mode-editor-subagent-prompt"]');
    expect((textarea.element as HTMLTextAreaElement).value).toBe('');
    const select = wrapper.get('[data-testid="mode-editor-subagent-placement"]');
    expect((select.element as HTMLSelectElement).value).toBe('append');
  });

  it('loads an existing mode\'s fragment and placement', () => {
    const wrapper = mount(ModeEditor, {
      props: {
        mode: {
          ...baseMode,
          subAgentPrompt: 'Fragment for children.',
          subAgentPromptPlacement: 'prepend' as const,
        },
        tools: [],
      },
    });

    const textarea = wrapper.get('[data-testid="mode-editor-subagent-prompt"]');
    expect((textarea.element as HTMLTextAreaElement).value).toBe('Fragment for children.');
    const select = wrapper.get('[data-testid="mode-editor-subagent-placement"]');
    expect((select.element as HTMLSelectElement).value).toBe('prepend');
  });

  it('saves the fragment and selected placement', async () => {
    const wrapper = mount(ModeEditor, { props: { mode: null, tools: [] } });

    await wrapper.get('[data-testid="mode-editor-name"]').setValue('New Mode');
    await wrapper.get('#mode-prompt').setValue('primary prompt');
    await wrapper.get('[data-testid="mode-editor-subagent-prompt"]').setValue('Fragment.');
    await wrapper.get('[data-testid="mode-editor-subagent-placement"]').setValue('prepend');
    await wrapper.get('form').trigger('submit');

    const data = lastSave(wrapper);
    expect(data.subAgentPrompt).toBe('Fragment.');
    expect(data.subAgentPromptPlacement).toBe('prepend');
  });

  it('omits both fields when the fragment is empty', async () => {
    const wrapper = mount(ModeEditor, { props: { mode: null, tools: [] } });

    await wrapper.get('[data-testid="mode-editor-name"]').setValue('New Mode');
    await wrapper.get('#mode-prompt').setValue('primary prompt');
    await wrapper.get('form').trigger('submit');

    const data = lastSave(wrapper);
    expect(data.subAgentPrompt).toBeUndefined();
    expect(data.subAgentPromptPlacement).toBeUndefined();
  });

  it('keeps an edited mode\'s existing placement when only the fragment changes', async () => {
    const wrapper = mount(ModeEditor, {
      props: {
        mode: {
          ...baseMode,
          subAgentPrompt: 'Old fragment.',
          subAgentPromptPlacement: 'prepend' as const,
        },
        tools: [],
      },
    });

    await wrapper.get('[data-testid="mode-editor-subagent-prompt"]').setValue('New fragment.');
    await wrapper.get('form').trigger('submit');

    const data = lastSave(wrapper);
    expect(data.subAgentPrompt).toBe('New fragment.');
    expect(data.subAgentPromptPlacement).toBe('prepend');
  });
});

describe('ModeEditor required sub-agent tools', () => {
  const catalog: ToolDefinition[] = [
    { name: 'claim-task', group: 'tasks', groupLabel: 'Task Management' },
    { name: 'list-tasks', group: 'tasks', groupLabel: 'Task Management' },
    { name: 'get_weather', group: 'sample', groupLabel: 'Sample Tools' },
    { name: 'web_search', group: 'builtin', groupLabel: 'Provider Built-ins' },
  ];

  const requiredSection = (wrapper: ReturnType<typeof mount>) =>
    wrapper.get('[data-testid="mode-editor-required-tools"]');

  async function fillRequired(wrapper: ReturnType<typeof mount>): Promise<void> {
    await wrapper.get('[data-testid="mode-editor-name"]').setValue('New Mode');
    await wrapper.get('#mode-prompt').setValue('primary prompt');
  }

  it('renders its own picker with the hint line, without the provider built-ins group', () => {
    const wrapper = mount(ModeEditor, { props: { mode: null, tools: catalog } });

    const section = requiredSection(wrapper);
    expect(section.get('[data-testid="mode-editor-required-tools-hint"]').text()).toContain(
      'guaranteed to every sub-agent'
    );
    expect(section.find('[data-testid="tool-claim-task"]').exists()).toBe(true);
    expect(section.find('[data-testid="tool-web_search"]').exists()).toBe(false);
  });

  it('loads an existing mode\'s required tools as ticked, independent of Enabled Tools', () => {
    const wrapper = mount(ModeEditor, {
      props: {
        mode: { ...baseMode, subAgentRequiredTools: ['claim-task'] },
        tools: catalog,
      },
    });

    const section = requiredSection(wrapper);
    const claim = section.get('[data-testid="tool-claim-task"]').element as HTMLInputElement;
    const list = section.get('[data-testid="tool-list-tasks"]').element as HTMLInputElement;
    expect(claim.checked).toBe(true);
    expect(list.checked).toBe(false);
  });

  it('saves the picked ids and round-trips them', async () => {
    const wrapper = mount(ModeEditor, { props: { mode: null, tools: catalog } });
    await fillRequired(wrapper);

    await requiredSection(wrapper).get('[data-testid="tool-claim-task"]').setValue(true);
    await requiredSection(wrapper).get('[data-testid="tool-list-tasks"]').setValue(true);
    await wrapper.get('form').trigger('submit');

    expect(lastSave(wrapper).subAgentRequiredTools).toEqual(['claim-task', 'list-tasks']);
  });

  it('omits the field entirely when nothing is picked — unset means "not enforced"', async () => {
    const wrapper = mount(ModeEditor, { props: { mode: null, tools: catalog } });
    await fillRequired(wrapper);

    await wrapper.get('form').trigger('submit');

    expect('subAgentRequiredTools' in lastSave(wrapper)).toBe(false);
  });

  it('deselecting the last required tool saves back to unset, not an empty list', async () => {
    const wrapper = mount(ModeEditor, {
      props: {
        mode: { ...baseMode, subAgentRequiredTools: ['claim-task'] },
        tools: catalog,
      },
    });

    await requiredSection(wrapper).get('[data-testid="tool-claim-task"]').setValue(false);
    await wrapper.get('form').trigger('submit');

    expect('subAgentRequiredTools' in lastSave(wrapper)).toBe(false);
  });

  it('preserves stored pattern ids the catalog cannot render (e.g. tasks:* from a copied mode)', async () => {
    const wrapper = mount(ModeEditor, {
      props: {
        mode: { ...baseMode, subAgentRequiredTools: ['tasks:*', 'claim-task'] },
        tools: catalog,
      },
    });

    // Interact with the picker so the save is not just echoing the loaded list.
    await requiredSection(wrapper).get('[data-testid="tool-list-tasks"]').setValue(true);
    await wrapper.get('form').trigger('submit');

    const saved = lastSave(wrapper).subAgentRequiredTools;
    expect(saved).toContain('tasks:*');
    expect(saved).toContain('claim-task');
    expect(saved).toContain('list-tasks');
  });

  it('does not leak required-tool picks into the Enabled Tools fields', async () => {
    const wrapper = mount(ModeEditor, { props: { mode: null, tools: catalog } });
    await fillRequired(wrapper);

    await requiredSection(wrapper).get('[data-testid="tool-claim-task"]').setValue(true);
    await wrapper.get('form').trigger('submit');

    const data = lastSave(wrapper);
    // A fresh mode with everything ticked keeps enabledTools undefined ("all tools").
    expect(data.enabledTools).toBeUndefined();
    expect(data.subAgentRequiredTools).toEqual(['claim-task']);
  });
});
