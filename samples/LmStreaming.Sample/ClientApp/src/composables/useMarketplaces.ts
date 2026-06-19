import { ref, computed } from 'vue';
import type { MarketplaceCatalog } from '@/types/marketplace';
import { listMarketplaces, MarketplaceGatewayUnavailableError } from '@/api/marketplacesApi';

/**
 * Loads the marketplace catalog (available plugins/skills/agents) from the backend proxy.
 *
 * The gateway is frequently offline — the catalog is a read-only browse and the backend never
 * spawns the gateway just to list it — so "offline" is a first-class, non-error state
 * (<see cref="isGatewayOffline"/>) the UI renders distinctly from a genuine failure.
 */
export function useMarketplaces() {
  const catalog = ref<MarketplaceCatalog | null>(null);
  const isLoading = ref(false);
  const isGatewayOffline = ref(false);
  const error = ref<string | null>(null);

  const marketplaces = computed(() => catalog.value?.marketplaces ?? []);
  const isEmpty = computed(
    () => catalog.value !== null && catalog.value.marketplaces.length === 0
  );

  /** (Re)loads the catalog, optionally narrowing to a subset of marketplace aliases. */
  async function load(aliases?: string[]): Promise<void> {
    isLoading.value = true;
    isGatewayOffline.value = false;
    error.value = null;
    try {
      catalog.value = await listMarketplaces(aliases);
    } catch (e) {
      catalog.value = null;
      if (e instanceof MarketplaceGatewayUnavailableError) {
        isGatewayOffline.value = true;
      } else {
        error.value = e instanceof Error ? e.message : 'Failed to load marketplaces';
        console.error('Failed to load marketplaces:', e);
      }
    } finally {
      isLoading.value = false;
    }
  }

  return { catalog, marketplaces, isLoading, isGatewayOffline, isEmpty, error, load };
}
