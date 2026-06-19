import { describe, it, expect, afterEach } from 'vitest';
import { mount, flushPromises } from '@vue/test-utils';
import MarketplaceBrowser from '@/components/MarketplaceBrowser.vue';
import {
  marketplacePreviewSubset,
  installMarketplacePreviewFetchMock,
} from '../fixtures/marketplacePreview';

describe('MarketplaceBrowser', () => {
  let restore: (() => void) | undefined;
  afterEach(() => restore?.());

  it('renders the captured catalog: marketplaces, plugins, skills and agents', async () => {
    const mock = installMarketplacePreviewFetchMock();
    restore = mock.restore;

    const wrapper = mount(MarketplaceBrowser);
    await flushPromises();

    // every marketplace from the fixture is rendered
    for (const mk of marketplacePreviewSubset.marketplaces) {
      expect(wrapper.find(`[data-testid="marketplace-item-${mk.alias}"]`).exists()).toBe(true);
    }
    // a known plugin with both skills and agents renders its chips
    expect(wrapper.find('[data-testid="marketplace-plugin-orleans-dev"]').exists()).toBe(true);
    expect(wrapper.find('[data-testid="marketplace-skill-orleans-patterns"]').exists()).toBe(true);
    expect(wrapper.find('[data-testid="marketplace-agent-orleans-reviewer"]').exists()).toBe(true);

    expect(wrapper.find('[data-testid="marketplace-browser-offline"]').exists()).toBe(false);
    expect(wrapper.find('[data-testid="marketplace-browser-error"]').exists()).toBe(false);
  });

  it('shows the offline state when the gateway returns 503', async () => {
    const mock = installMarketplacePreviewFetchMock({ status: 503, body: { error: 'offline' } });
    restore = mock.restore;

    const wrapper = mount(MarketplaceBrowser);
    await flushPromises();

    expect(wrapper.find('[data-testid="marketplace-browser-offline"]').exists()).toBe(true);
    expect(wrapper.find('[data-testid="marketplace-browser-error"]').exists()).toBe(false);
  });

  it('shows a generic error state on other failures', async () => {
    const mock = installMarketplacePreviewFetchMock({ status: 500, body: { error: 'boom' } });
    restore = mock.restore;

    const wrapper = mount(MarketplaceBrowser);
    await flushPromises();

    expect(wrapper.find('[data-testid="marketplace-browser-error"]').exists()).toBe(true);
  });

  it('shows the empty state when no marketplaces are configured', async () => {
    const mock = installMarketplacePreviewFetchMock({ catalog: { selected: [], marketplaces: [] } });
    restore = mock.restore;

    const wrapper = mount(MarketplaceBrowser);
    await flushPromises();

    expect(wrapper.find('[data-testid="marketplace-browser-empty"]').exists()).toBe(true);
  });

  it('refetches when the refresh button is clicked', async () => {
    const mock = installMarketplacePreviewFetchMock();
    restore = mock.restore;

    const wrapper = mount(MarketplaceBrowser);
    await flushPromises();
    expect(mock.fetchSpy).toHaveBeenCalledTimes(1);

    await wrapper.find('[data-testid="marketplace-browser-refresh"]').trigger('click');
    await flushPromises();

    expect(mock.fetchSpy).toHaveBeenCalledTimes(2);
  });
});
