import { describe, expect, it } from 'vitest';
import { mount } from '@vue/test-utils';
import ProviderSelector from '@/components/ProviderSelector.vue';
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
