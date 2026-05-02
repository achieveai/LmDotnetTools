import { ref, computed } from 'vue';
import type { ProviderDescriptor } from '@/types/providers';
import { listProviders } from '@/api/providersApi';

/**
 * Composable that loads the provider catalog from the backend and exposes the
 * user's currently-selected provider for the next new conversation.
 *
 * The selection is intentionally process-local: the backend treats a thread's
 * provider as immutable once the thread has its first message, so this state
 * only matters until that point. After the first message we read the locked
 * value from the conversation's metadata instead.
 */
export function useProviders() {
  const providers = ref<ProviderDescriptor[]>([]);
  const defaultProviderId = ref<string | null>(null);
  const selectedProviderId = ref<string | null>(null);
  const isLoading = ref(false);
  const error = ref<string | null>(null);

  /**
   * Provider currently chosen for the next new conversation.
   */
  const selectedProvider = computed(() =>
    providers.value.find((p) => p.id === selectedProviderId.value) ?? null
  );

  /**
   * Loads the provider catalog. Selects the backend-supplied default if the
   * user has not yet picked one, falling back to the first available provider.
   */
  async function loadProviders(): Promise<void> {
    isLoading.value = true;
    error.value = null;
    try {
      const response = await listProviders();
      providers.value = response.providers ?? [];
      defaultProviderId.value = response.default ?? null;

      if (selectedProviderId.value === null) {
        const initial =
          providers.value.find((p) => p.id === defaultProviderId.value && p.available)?.id
          ?? providers.value.find((p) => p.available)?.id
          ?? defaultProviderId.value
          ?? null;
        selectedProviderId.value = initial;
      }
    } catch (e) {
      error.value = e instanceof Error ? e.message : 'Failed to load providers';
      console.error('Failed to load providers:', e);
    } finally {
      isLoading.value = false;
    }
  }

  /**
   * Selects a provider for new conversations. No-op for unknown ids so the UI
   * can defensively pass user input without leaving the dropdown in a stale
   * state.
   */
  function selectProvider(providerId: string): void {
    if (!providers.value.some((p) => p.id === providerId)) {
      return;
    }
    selectedProviderId.value = providerId;
  }

  /**
   * Look up a descriptor by id. Returns null if the id is unknown — useful for
   * rendering a locked-thread badge when the persisted provider has since been
   * removed from the registry.
   */
  function getProviderById(providerId: string | null | undefined): ProviderDescriptor | null {
    if (!providerId) return null;
    return providers.value.find((p) => p.id === providerId) ?? null;
  }

  return {
    providers,
    defaultProviderId,
    selectedProviderId,
    selectedProvider,
    isLoading,
    error,
    loadProviders,
    selectProvider,
    getProviderById,
  };
}
