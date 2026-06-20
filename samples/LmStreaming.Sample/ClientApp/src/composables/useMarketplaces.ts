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

  // Tracks the in-flight catalog fetch so a re-fetch (or unmount via cleanup) cancels the previous
  // request rather than leaking it and racing to write a stale result/error.
  let abortController: AbortController | null = null;

  /** (Re)loads the catalog, optionally narrowing to a subset of marketplace aliases. */
  async function load(aliases?: string[]): Promise<void> {
    // Cancel any prior in-flight fetch before starting a new one.
    abortController?.abort();
    const controller = new AbortController();
    abortController = controller;

    isLoading.value = true;
    isGatewayOffline.value = false;
    error.value = null;
    try {
      catalog.value = await listMarketplaces(aliases, controller.signal);
    } catch (e) {
      // A fetch we deliberately aborted must not surface as an error or clobber state.
      if (controller.signal.aborted) {
        return;
      }
      catalog.value = null;
      if (e instanceof MarketplaceGatewayUnavailableError) {
        isGatewayOffline.value = true;
      } else {
        error.value = e instanceof Error ? e.message : 'Failed to load marketplaces';
        console.error('Failed to load marketplaces:', e);
      }
    } finally {
      // Only the latest request owns the loading flag; an aborted-and-superseded request must not
      // flip it off out from under the request that replaced it.
      if (abortController === controller) {
        isLoading.value = false;
      }
    }
  }

  /** Cancels any in-flight catalog fetch; call from the consumer's unmount hook. */
  function cleanup(): void {
    abortController?.abort();
  }

  return { catalog, marketplaces, isLoading, isGatewayOffline, isEmpty, error, load, cleanup };
}
