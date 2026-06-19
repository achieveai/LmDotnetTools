import { describe, it, expect, afterEach } from 'vitest';
import {
  marketplacePreviewSubset,
  marketplacePreviewJson,
  marketplaceUnknownAliasError,
  installMarketplacePreviewFetchMock,
  type MarketplaceCatalog,
} from '../fixtures/marketplacePreview';

describe('marketplace-preview fixture', () => {
  it('is a structurally complete catalog subset', () => {
    expect(marketplacePreviewSubset.selected.length).toBeGreaterThan(0);
    expect(marketplacePreviewSubset.marketplaces.length).toBeGreaterThan(0);
  });

  it('keeps `selected` in sync with the marketplace aliases it carries', () => {
    const aliases = marketplacePreviewSubset.marketplaces.map((m) => m.alias).sort();
    expect([...marketplacePreviewSubset.selected].sort()).toEqual(aliases);
  });

  it('every skill and agent carries the full contract fields and back-references its plugin', () => {
    for (const mk of marketplacePreviewSubset.marketplaces) {
      expect(mk.error).toBeNull();
      for (const plugin of mk.plugins) {
        for (const item of [...plugin.skills, ...plugin.agents]) {
          expect(item.name.length).toBeGreaterThan(0);
          expect(typeof item.description).toBe('string');
          expect(item.plugin).toBe(plugin.name);
          expect(item.marketplace).toBe(mk.alias);
          expect(item.path.startsWith(`/marketplaces/${mk.alias}/`)).toBe(true);
        }
      }
    }
  });

  it('covers the representative shapes UI rendering must handle', () => {
    const plugins = marketplacePreviewSubset.marketplaces.flatMap((m) => m.plugins);
    // a plugin with BOTH skills and agents (mixed rendering)
    expect(plugins.some((p) => p.skills.length > 0 && p.agents.length > 0)).toBe(true);
    // a skills-only plugin (empty agents array, not missing)
    expect(plugins.some((p) => p.skills.length > 0 && p.agents.length === 0)).toBe(true);
    // a plugin with a null version (must not render as "null"/empty string)
    expect(plugins.some((p) => p.version === null)).toBe(true);
  });

  it('round-trips through the pre-serialized JSON body', () => {
    const parsed = JSON.parse(marketplacePreviewJson) as MarketplaceCatalog;
    expect(parsed).toEqual(marketplacePreviewSubset);
  });

  it('models the gateway 400 unknown-alias envelope', () => {
    expect(marketplaceUnknownAliasError.code).toBe(400);
    expect(marketplaceUnknownAliasError.unknown).toContain('nope');
    expect(marketplaceUnknownAliasError.available).toEqual(
      marketplacePreviewSubset.selected.slice().sort()
    );
  });
});

describe('installMarketplacePreviewFetchMock', () => {
  let restore: (() => void) | undefined;
  afterEach(() => restore?.());

  it('serves the captured catalog through a stubbed fetch', async () => {
    const mock = installMarketplacePreviewFetchMock();
    restore = mock.restore;

    const res = await fetch('/api/marketplaces/preview');
    const body = (await res.json()) as MarketplaceCatalog;

    expect(res.status).toBe(200);
    expect(mock.fetchSpy).toHaveBeenCalledOnce();
    expect(body.marketplaces.map((m) => m.alias)).toEqual(
      marketplacePreviewSubset.marketplaces.map((m) => m.alias)
    );
  });

  it('can serve a caller-supplied catalog (e.g. an empty result)', async () => {
    const empty: MarketplaceCatalog = { selected: [], marketplaces: [] };
    const mock = installMarketplacePreviewFetchMock({ catalog: empty });
    restore = mock.restore;

    const body = (await (await fetch('/whatever')).json()) as MarketplaceCatalog;
    expect(body).toEqual(empty);
  });
});
