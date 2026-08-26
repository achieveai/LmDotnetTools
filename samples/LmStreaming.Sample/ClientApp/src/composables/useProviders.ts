import { ref, computed } from 'vue';
import type { ProviderDescriptor } from '@/types/providers';
import { listProviders, switchConversationProvider } from '@/api/providersApi';

/**
 * Composable that loads the provider catalog from the backend and exposes the
 * user's currently-selected provider.
 *
 * For a NEW (messageless) conversation the selection is process-local and simply
 * chooses the provider the first message will bind. Once a conversation has started
 * its provider is mutable ONLY while idle: switching it calls {@link switchProvider}
 * (POST .../provider), which recreates the agent on the backend. While a run streams
 * the selector is locked (the backend answers 409).
 */
/**
 * How many times {@link useProviders.settleCatalog} follows the chain of superseding loads before
 * giving up. Mirrors the bound in `useWorkspaces` for the same reason: the loop only iterates when a
 * load is superseded WHILE being awaited, so it converges as soon as one load finishes as the
 * newest, and the bound keeps a caller that keeps starting loads from hanging whoever is waiting.
 */
const MAX_CATALOG_SETTLE_WAITS = 5;

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
   * Monotonic id of the most recently STARTED load, and a handle on it. Together they let a caller
   * wait for the load that will actually win rather than merely its own — see {@link settleCatalog}.
   */
  let loadGeneration = 0;
  let newestLoad: Promise<void> = Promise.resolve();

  /**
   * Loads the provider catalog. Selects the backend-supplied default if the
   * user has not yet picked one, falling back to the first available provider.
   *
   * The returned promise means "this call finished", NOT "this call applied its response": a
   * superseded load resolves having written nothing. A caller that must read a settled selection
   * has to follow up with {@link settleCatalog}.
   */
  function loadProviders(): Promise<void> {
    const load = runLoad();
    // Assigned synchronously, in the same tick in which `runLoad` bumped `loadGeneration` (an async
    // function body runs up to its first await before returning), so the counter and this handle can
    // never disagree about which load is newest.
    newestLoad = load;
    return load;
  }

  async function runLoad(): Promise<void> {
    const generation = ++loadGeneration;
    isLoading.value = true;
    error.value = null;
    try {
      const response = await listProviders();
      if (generation !== loadGeneration) return;
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
      if (generation !== loadGeneration) return;
      error.value = e instanceof Error ? e.message : 'Failed to load providers';
      console.error('Failed to load providers:', e);
    } finally {
      if (generation === loadGeneration) {
        isLoading.value = false;
      }
    }
  }

  /**
   * Waits until the newest load has applied its response, and reports whether it got there.
   *
   * The catalog is fetched on mount while the composer is ALREADY interactive, so a first send can
   * easily beat the response — and until it lands `selectedProviderId` is null. Reading it in that
   * window and refusing would tell the user to "choose a provider" when the picker has not even
   * been populated yet. Callers that need a settled selection await this first.
   *
   * Returns false if `MAX_CATALOG_SETTLE_WAITS` passes are spent without converging, so the caller
   * degrades deliberately instead of waiting forever.
   */
  async function settleCatalog(): Promise<boolean> {
    for (let attempt = 0; attempt < MAX_CATALOG_SETTLE_WAITS; attempt++) {
      const generation = loadGeneration;
      await newestLoad;
      // No newer load started while we waited, so the one we awaited was still the latest when it
      // completed — which is exactly the condition under which it applied its response.
      if (loadGeneration === generation) {
        return true;
      }
    }
    return false;
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
   * Switches the given (started) conversation's provider on the backend, then reflects it locally.
   * Mirrors useChatModes.switchMode. Re-throws so the caller (ChatLayout) can surface the failure
   * and leave the selection unchanged — the backend answers 409 while streaming and 503 when the
   * target provider is unavailable.
   */
  async function switchProvider(threadId: string, providerId: string): Promise<void> {
    try {
      await switchConversationProvider(threadId, providerId);
      selectedProviderId.value = providerId;
    } catch (e) {
      error.value = e instanceof Error ? e.message : 'Failed to switch provider';
      console.error('Failed to switch provider:', e);
      throw e;
    }
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
    settleCatalog,
    selectProvider,
    switchProvider,
    getProviderById,
  };
}
