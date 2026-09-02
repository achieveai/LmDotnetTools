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

  it('writes an explicit null for both fields when the fragment is empty', async () => {
    const wrapper = mount(ModeEditor, { props: { mode: null, tools: [] } });

    await wrapper.get('[data-testid="mode-editor-name"]').setValue('New Mode');
    await wrapper.get('#mode-prompt').setValue('primary prompt');
    await wrapper.get('form').trigger('submit');

    const data = lastSave(wrapper);
    // subAgentPrompt writes explicit null (the server's presence-aware contract reads an omitted
    // key as "leave the stored fragment alone"). subAgentPromptPlacement has no persisted "clear"
    // meaning of its own, so it stays omitted.
    expect(data.subAgentPrompt).toBeNull();
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

  it('blanking an existing mode\'s fragment saves an explicit null, not omission', async () => {
    // Omitting the key on update would preserve the mode's stored fragment (the server's
    // presence-aware contract), so clearing it requires the literal JSON null to survive the wire.
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

    await wrapper.get('[data-testid="mode-editor-subagent-prompt"]').setValue('');
    await wrapper.get('form').trigger('submit');

    const data = lastSave(wrapper);
    expect(data.subAgentPrompt).toBeNull();
    const wire = JSON.parse(JSON.stringify(data));
    expect('subAgentPrompt' in wire).toBe(true);
    expect(wire.subAgentPrompt).toBeNull();
  });
});

describe('ModeEditor description', () => {
  it('blanking an existing mode\'s description saves an explicit null, not omission', async () => {
    // baseMode.description is 'A user mode'. Omitting the key on update would preserve it (the
    // server's presence-aware contract), so clearing requires the literal JSON null.
    const wrapper = mount(ModeEditor, { props: { mode: baseMode, tools: [] } });

    await wrapper.get('#mode-description').setValue('');
    await wrapper.get('form').trigger('submit');

    const data = lastSave(wrapper);
    expect(data.description).toBeNull();
    const wire = JSON.parse(JSON.stringify(data));
    expect('description' in wire).toBe(true);
    expect(wire.description).toBeNull();
  });

  it('keeps a non-empty description unchanged on save', async () => {
    const wrapper = mount(ModeEditor, { props: { mode: baseMode, tools: [] } });

    await wrapper.get('form').trigger('submit');

    expect(lastSave(wrapper).description).toBe('A user mode');
  });
});

describe('ModeEditor sub-agent routing policy', () => {
  it('preserves policy fields that the form does not render when editing a mode', async () => {
    const policy = {
      subAgentReasoningEffort: 'xhigh',
      subAgentModelIntelligenceByType: {
        'code-reviewer:architecture-review': 5,
        'code-reviewer:test-coverage-review': 3,
      },
      defaultSubAgentModelIntelligence: 3,
    };
    const wrapper = mount(ModeEditor, {
      props: {
        mode: { ...baseMode, ...policy },
        tools: [],
      },
    });

    await wrapper.get('[data-testid="mode-editor-name"]').setValue('Renamed Mode');
    await wrapper.get('form').trigger('submit');

    expect(lastSave(wrapper)).toMatchObject(policy);
  });
});

describe('ModeEditor required sub-agent tools', () => {
  const catalog: ToolDefinition[] = [
    { name: 'claim-task', group: 'tasks', groupLabel: 'Task Management' },
    { name: 'list-tasks', group: 'tasks', groupLabel: 'Task Management' },
    { name: 'get_weather', group: 'sample', groupLabel: 'Sample Tools' },
    { name: 'web_search', group: 'builtin', groupLabel: 'Provider Built-ins' },
    {
      name: 'All workspace tools',
      id: 'sandbox:*',
      group: 'sandbox',
      groupLabel: 'Workspace',
      isWildcard: true,
      requiresSandbox: true,
    },
    { name: 'Read', id: 'sandbox:Read', group: 'sandbox', groupLabel: 'Workspace', requiresSandbox: true },
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

  // PR #626 review F-001: the server resolves a `sandbox:*` requirement to nothing (the sandbox
  // roster is live-gateway-only), so offering the row here would let the picker create a silently
  // inert requirement — the exact #623 failure shape this picker exists to eliminate.
  it('excludes the sandbox group, whose requirements the server cannot resolve', () => {
    const wrapper = mount(ModeEditor, { props: { mode: null, tools: catalog } });

    const section = requiredSection(wrapper);
    expect(section.find('[data-testid="tool-sandbox:*"]').exists()).toBe(false);
    expect(section.find('[data-testid="tool-sandbox:Read"]').exists()).toBe(false);
    expect(section.find('[data-testid="tool-group-sandbox"]').exists()).toBe(false);

    // The same rows stay available where they belong: the Enabled Tools picker.
    expect(wrapper.find('[data-testid="tool-sandbox:*"]').exists()).toBe(true);
    expect(wrapper.find('[data-testid="tool-sandbox:Read"]').exists()).toBe(true);
  });

  it('preserves a stored sandbox requirement it cannot render instead of dropping it', async () => {
    const wrapper = mount(ModeEditor, {
      props: {
        mode: { ...baseMode, subAgentRequiredTools: ['sandbox:*', 'claim-task'] },
        tools: catalog,
      },
    });

    await requiredSection(wrapper).get('[data-testid="tool-list-tasks"]').setValue(true);
    await wrapper.get('form').trigger('submit');

    const saved = lastSave(wrapper).subAgentRequiredTools;
    expect(saved).toContain('sandbox:*');
    expect(saved).toContain('claim-task');
    expect(saved).toContain('list-tasks');
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

  it('writes an explicit null when nothing is picked — unset means "not enforced"', async () => {
    const wrapper = mount(ModeEditor, { props: { mode: null, tools: catalog } });
    await fillRequired(wrapper);

    await wrapper.get('form').trigger('submit');

    expect(lastSave(wrapper).subAgentRequiredTools).toBeNull();
  });

  it('deselecting the last required tool saves an explicit null, not an omitted key', async () => {
    // The server's presence-aware update contract preserves the stored selection when the key is
    // omitted, so disabling enforcement after unchecking everything requires the literal null.
    const wrapper = mount(ModeEditor, {
      props: {
        mode: { ...baseMode, subAgentRequiredTools: ['claim-task'] },
        tools: catalog,
      },
    });

    await requiredSection(wrapper).get('[data-testid="tool-claim-task"]').setValue(false);
    await wrapper.get('form').trigger('submit');

    const data = lastSave(wrapper);
    expect(data.subAgentRequiredTools).toBeNull();
    const wire = JSON.parse(JSON.stringify(data));
    expect('subAgentRequiredTools' in wire).toBe(true);
    expect(wire.subAgentRequiredTools).toBeNull();
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
    // A fresh mode with everything ticked writes an explicit null for enabledTools ("all tools").
    expect(data.enabledTools).toBeNull();
    expect(data.subAgentRequiredTools).toEqual(['claim-task']);
  });

  it('re-ticking every Enabled Tool on an edited mode writes an explicit null, not omission', async () => {
    // baseMode starts with a narrower enabledTools: ['get_weather'] — an explicit selection, not
    // the "all tools" default. Ticking the remaining catalog tool (`web_search` is built-in and
    // excluded; `claim-task`/`list-tasks` are in the tasks group) must serialize an explicit null,
    // not silently preserve the old narrower list via an omitted key.
    const wrapper = mount(ModeEditor, {
      props: {
        mode: { ...baseMode, enabledTools: ['get_weather'] },
        tools: catalog,
      },
    });

    // Enabled Tools renders before Required Sub-agent Tools in the template, so index 0 is the
    // Enabled Tools picker's checkbox for a tasks-group tool that also appears in the required
    // picker (both non-built-in, non-sandbox groups).
    const enabledClaimTask = wrapper.findAll('[data-testid="tool-claim-task"]')[0];
    const enabledListTasks = wrapper.findAll('[data-testid="tool-list-tasks"]')[0];
    await enabledClaimTask.setValue(true);
    await enabledListTasks.setValue(true);
    await wrapper.get('form').trigger('submit');

    const data = lastSave(wrapper);
    expect(data.enabledTools).toBeNull();
    const wire = JSON.parse(JSON.stringify(data));
    expect('enabledTools' in wire).toBe(true);
    expect(wire.enabledTools).toBeNull();
  });
});
