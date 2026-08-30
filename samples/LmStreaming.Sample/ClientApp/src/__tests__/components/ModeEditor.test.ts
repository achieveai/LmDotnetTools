import { describe, expect, it } from 'vitest';
import { mount } from '@vue/test-utils';
import ModeEditor from '@/components/ModeEditor.vue';
import type { ChatMode, ChatModeCreateUpdate } from '@/types/chatMode';

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
