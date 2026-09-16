import { ref } from 'vue';

export type ViewPreference = 'consumer' | 'developer';

export const DEFAULT_VIEW_PREFERENCE: ViewPreference = 'consumer';
export const VIEW_PREFERENCE_STORAGE_KEY = 'lmstreaming:view-preference';

function readViewPreference(): ViewPreference {
  try {
    const saved = localStorage.getItem(VIEW_PREFERENCE_STORAGE_KEY);
    return saved === 'consumer' || saved === 'developer' ? saved : DEFAULT_VIEW_PREFERENCE;
  } catch {
    return DEFAULT_VIEW_PREFERENCE;
  }
}

export function useViewPreference() {
  const viewPreference = ref<ViewPreference>(readViewPreference());

  function selectViewPreference(value: ViewPreference): void {
    viewPreference.value = value;
    try {
      localStorage.setItem(VIEW_PREFERENCE_STORAGE_KEY, value);
    } catch {
      // The in-memory preference remains usable when storage is unavailable.
    }
  }

  return { viewPreference, selectViewPreference };
}
