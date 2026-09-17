import { describe, expect, it } from 'vitest';
import { mount } from '@vue/test-utils';
import ModeSelector from '@/components/ModeSelector.vue';

const modes = [
  {
    id: 'default',
    name: 'General Assistant',
    description: 'General mode',
    systemPrompt: 'You are helpful.',
    enabledTools: undefined,
    isSystemDefined: true,
    createdAt: 0,
    updatedAt: 0,
  },
  {
    id: 'math-helper',
    name: 'Math Helper',
    description: 'Math mode',
    systemPrompt: 'Use calculator.',
    enabledTools: ['calculate'],
    isSystemDefined: true,
    createdAt: 0,
    updatedAt: 0,
  },
];

describe('ModeSelector', () => {
  it('disables selector button when disabled is true', () => {
    const wrapper = mount(ModeSelector, {
      props: {
        modes,
        currentModeId: 'default',
        tools: [],
        disabled: true,
      },
    });

    const button = wrapper.get('.selector-btn');
    expect(button.attributes('disabled')).toBeDefined();
  });

  it('does not open dropdown when disabled is true', async () => {
    const wrapper = mount(ModeSelector, {
      props: {
        modes,
        currentModeId: 'default',
        tools: [],
        disabled: true,
      },
    });

    await wrapper.get('.selector-btn').trigger('click');
    expect(wrapper.find('.dropdown-menu').exists()).toBe(false);
  });

  it('does not emit select-mode when disabled', async () => {
    const wrapper = mount(ModeSelector, {
      props: {
        modes,
        currentModeId: 'default',
        tools: [],
        disabled: true,
      },
    });

    await wrapper.get('.selector-btn').trigger('click');
    expect(wrapper.emitted('select-mode')).toBeUndefined();
  });

  it('opens the dropdown when it is NOT disabled, so the checks above are not vacuous', async () => {
    const wrapper = mount(ModeSelector, {
      props: { modes, currentModeId: 'default', tools: [], disabled: false },
    });

    await wrapper.get('.selector-btn').trigger('click');

    expect(wrapper.find('.dropdown-menu').exists()).toBe(true);
    await wrapper.get('[data-testid="mode-option-math-helper"]').trigger('click');
    expect(wrapper.emitted('select-mode')).toEqual([['math-helper']]);
  });
});

describe('ModeSelector disabled flipping mid-edit', () => {
  async function openManageModal() {
    const wrapper = mount(ModeSelector, {
      props: { modes, currentModeId: 'default', tools: [], disabled: false },
      attachTo: document.body,
    });
    await wrapper.get('.selector-btn').trigger('click');
    await wrapper.get('.manage-item').trigger('click');
    expect(wrapper.find('[data-testid="mode-management-modal"]').exists()).toBe(true);
    return wrapper;
  }

  it('keeps the management modal open when disabled flips true', async () => {
    // `disabled` folds in self-reversing socket conditions (`hasPendingClientQuestion`). Tearing the
    // modal down on that flip discarded whatever the user had typed — and the error about to be shown.
    const wrapper = await openManageModal();

    await wrapper.setProps({ disabled: true });

    expect(wrapper.find('[data-testid="mode-management-modal"]').exists()).toBe(true);
    wrapper.unmount();
  });

  it('still closes the dropdown when disabled flips true', async () => {
    const wrapper = mount(ModeSelector, {
      props: { modes, currentModeId: 'default', tools: [], disabled: false },
    });
    await wrapper.get('.selector-btn').trigger('click');
    expect(wrapper.find('.dropdown-menu').exists()).toBe(true);

    await wrapper.setProps({ disabled: true });

    expect(wrapper.find('.dropdown-menu').exists()).toBe(false);
  });

  it('closes the modal on an explicit close, so it is not simply un-closable', async () => {
    const wrapper = await openManageModal();

    await wrapper.findComponent({ name: 'ModeManagementModal' }).vm.$emit('close');

    expect(wrapper.find('[data-testid="mode-management-modal"]').exists()).toBe(false);
    wrapper.unmount();
  });
});
