import { describe, expect, it } from 'vitest';
import { mount } from '@vue/test-utils';
import ProviderSelector from '@/components/ProviderSelector.vue';
// Raw component source, used to assert the scoped stylesheet rules (scoped <style> is not
// injected into the jsdom DOM by @vue/test-utils, so we verify the CSS at the source level).
import providerSelectorSource from '@/components/ProviderSelector.vue?raw';
import type { ProviderDescriptor } from '@/types/providers';

const providers: ProviderDescriptor[] = [
  { id: 'openai', displayName: 'OpenAI', available: true },
  { id: 'anthropic', displayName: 'Anthropic', available: true },
  { id: 'claude-opus-4.8', displayName: 'Claude Opus 4.8', available: true, group: 'Copilot · Anthropic' },
  { id: 'claude-sonnet-5', displayName: 'Claude Sonnet 5', available: true, group: 'Copilot · Anthropic' },
  { id: 'gpt-5.5', displayName: 'GPT-5.5', available: true, group: 'Copilot · OpenAI' },
];

function open() {
  const wrapper = mount(ProviderSelector, {
    props: { providers, selectedProviderId: 'openai' },
  });
  return wrapper.get('.selector-btn').trigger('click').then(() => wrapper);
}

describe('ProviderSelector grouping', () => {
  it('renders a section header per partition group', async () => {
    const wrapper = await open();

    const headers = wrapper.findAll('.menu-group-header').map((h) => h.text());
    expect(headers).toEqual(['Copilot · Anthropic', 'Copilot · OpenAI']);
  });

  it('renders ungrouped providers ahead of the grouped sections', async () => {
    const wrapper = await open();

    // Ungrouped providers (openai/anthropic) have no header; the first header appears only after them.
    const menu = wrapper.get('.dropdown-menu');
    const html = menu.html();
    const firstHeaderIndex = html.indexOf('menu-group-header');
    const openAiIndex = html.indexOf('provider-option-openai');
    expect(openAiIndex).toBeGreaterThanOrEqual(0);
    expect(openAiIndex).toBeLessThan(firstHeaderIndex);
  });

  it('renders every provider as a selectable option under its group', async () => {
    const wrapper = await open();

    for (const provider of providers) {
      expect(wrapper.find(`[data-testid="provider-option-${provider.id}"]`).exists()).toBe(true);
    }
  });

  it('emits select-provider with the model id when a grouped option is clicked', async () => {
    const wrapper = await open();

    await wrapper.get('[data-testid="provider-option-gpt-5.5"]').trigger('click');
    expect(wrapper.emitted('select-provider')?.[0]).toEqual(['gpt-5.5']);
  });

  it('renders a flat list with no headers when no provider has a group', async () => {
    const wrapper = mount(ProviderSelector, {
      props: {
        providers: [
          { id: 'openai', displayName: 'OpenAI', available: true },
          { id: 'test', displayName: 'Test', available: true },
        ],
        selectedProviderId: 'openai',
      },
    });
    await wrapper.get('.selector-btn').trigger('click');

    expect(wrapper.findAll('.menu-group-header')).toHaveLength(0);
    expect(wrapper.findAll('.menu-item')).toHaveLength(2);
  });
});

describe('ProviderSelector disabled state', () => {
  // While a run streams the parent passes disabled=true; provider is editable only when idle.
  it('disables the selector button when disabled is true', () => {
    const wrapper = mount(ProviderSelector, {
      props: { providers, selectedProviderId: 'openai', disabled: true },
    });
    expect(wrapper.get('.selector-btn').attributes('disabled')).toBeDefined();
  });

  it('does not open the dropdown when disabled is true', async () => {
    const wrapper = mount(ProviderSelector, {
      props: { providers, selectedProviderId: 'openai', disabled: true },
    });
    await wrapper.get('.selector-btn').trigger('click');
    expect(wrapper.find('.dropdown-menu').exists()).toBe(false);
  });

  it('does not emit select-provider when disabled', async () => {
    const wrapper = mount(ProviderSelector, {
      props: { providers, selectedProviderId: 'openai', disabled: true },
    });
    await wrapper.get('.selector-btn').trigger('click');
    expect(wrapper.emitted('select-provider')).toBeUndefined();
  });

  it('renders an editable dropdown (no permanent lock badge) when idle', async () => {
    const wrapper = mount(ProviderSelector, {
      props: { providers, selectedProviderId: 'openai' },
    });
    // The old immutable-provider badge is gone; the selector is always a dropdown button.
    expect(wrapper.find('[data-testid="provider-locked-badge"]').exists()).toBe(false);
    expect(wrapper.get('.selector-btn').attributes('disabled')).toBeUndefined();
    await wrapper.get('.selector-btn').trigger('click');
    expect(wrapper.find('.dropdown-menu').exists()).toBe(true);
  });
});

describe('ProviderSelector dropdown scroll', () => {
  // Extract the `.dropdown-menu { ... }` rule body from the scoped stylesheet source.
  const menuRule = (() => {
    const start = providerSelectorSource.indexOf('.dropdown-menu {');
    expect(start).toBeGreaterThanOrEqual(0);
    const end = providerSelectorSource.indexOf('}', start);
    return providerSelectorSource.slice(start, end);
  })();

  it('caps the dropdown height so a long list does not run off-screen', () => {
    // max-height keeps the menu bounded; a bare `max-height` without a value would not.
    expect(menuRule).toMatch(/max-height:\s*[^;]+;/);
  });

  it('enables vertical scrolling for overflowing content', () => {
    expect(menuRule).toMatch(/overflow-y:\s*auto\s*;/);
  });
});
