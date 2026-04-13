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
});
