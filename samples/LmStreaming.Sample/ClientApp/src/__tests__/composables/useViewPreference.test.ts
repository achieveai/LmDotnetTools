import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import {
  DEFAULT_VIEW_PREFERENCE,
  VIEW_PREFERENCE_STORAGE_KEY,
  useViewPreference,
} from '@/composables/useViewPreference';

describe('useViewPreference', () => {
  beforeEach(() => localStorage.clear());

  afterEach(() => vi.restoreAllMocks());

  it('defaults to Consumer when no preference has been saved', () => {
    expect(useViewPreference().viewPreference.value).toBe(DEFAULT_VIEW_PREFERENCE);
    expect(DEFAULT_VIEW_PREFERENCE).toBe('consumer');
  });

  it('restores and persists a valid preference', () => {
    localStorage.setItem(VIEW_PREFERENCE_STORAGE_KEY, 'developer');
    const preference = useViewPreference();

    expect(preference.viewPreference.value).toBe('developer');

    preference.selectViewPreference('consumer');
    expect(localStorage.getItem(VIEW_PREFERENCE_STORAGE_KEY)).toBe('consumer');
  });

  it('falls back to Consumer for an invalid saved value', () => {
    localStorage.setItem(VIEW_PREFERENCE_STORAGE_KEY, 'expert');

    expect(useViewPreference().viewPreference.value).toBe('consumer');
  });

  it('keeps working in memory when storage reads or writes fail', () => {
    vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => {
      throw new DOMException('blocked');
    });
    const preference = useViewPreference();
    expect(preference.viewPreference.value).toBe('consumer');

    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
      throw new DOMException('blocked');
    });
    expect(() => preference.selectViewPreference('developer')).not.toThrow();
    expect(preference.viewPreference.value).toBe('developer');
  });
});
