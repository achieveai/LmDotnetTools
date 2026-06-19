import { describe, it, expect, afterEach } from 'vitest';
import { listMarketplaces, MarketplaceGatewayUnavailableError } from '@/api/marketplacesApi';
import {
  marketplacePreviewSubset,
  installMarketplacePreviewFetchMock,
} from '../fixtures/marketplacePreview';

describe('marketplacesApi.listMarketplaces', () => {
  let restore: (() => void) | undefined;
  afterEach(() => restore?.());

  it('returns the catalog on success', async () => {
    const mock = installMarketplacePreviewFetchMock();
    restore = mock.restore;

    const catalog = await listMarketplaces();

    expect(catalog.marketplaces.map((m) => m.alias)).toEqual(
      marketplacePreviewSubset.marketplaces.map((m) => m.alias)
    );
  });

  it('requests the proxy with no query when no aliases are given', async () => {
    const mock = installMarketplacePreviewFetchMock();
    restore = mock.restore;

    await listMarketplaces();

    expect(mock.fetchSpy).toHaveBeenCalledWith('/api/marketplaces');
  });

  it('passes a comma-separated, url-encoded marketplaces query', async () => {
    const mock = installMarketplacePreviewFetchMock();
    restore = mock.restore;

    await listMarketplaces(['official', 'claude_plugins']);

    expect(mock.fetchSpy).toHaveBeenCalledWith(
      '/api/marketplaces?marketplaces=official%2Cclaude_plugins'
    );
  });

  it('throws MarketplaceGatewayUnavailableError on 503 (gateway offline)', async () => {
    const mock = installMarketplacePreviewFetchMock({ status: 503, body: { error: 'offline' } });
    restore = mock.restore;

    await expect(listMarketplaces()).rejects.toBeInstanceOf(MarketplaceGatewayUnavailableError);
  });

  it('throws a generic error on other non-ok statuses', async () => {
    const mock = installMarketplacePreviewFetchMock({ status: 500, body: { error: 'boom' } });
    restore = mock.restore;

    await expect(listMarketplaces()).rejects.toThrow(/Failed to fetch marketplaces/);
  });
});
