import { describe, it, expect } from 'vitest';
import { isAuthEventPayload, isPredefinedKeyProvider } from '@/types/auth';

/**
 * `isAuthEventPayload` is the single routing gate for the whole deferred-auth client feature and
 * runs BEFORE the done/error sniff in wsClient. A false negative drops the banner; a false positive
 * eats a real chat message. Pin both directions.
 */
describe('isAuthEventPayload', () => {
  it('returns true for an auth_required frame', () => {
    expect(
      isAuthEventPayload('{"$type":"auth_required","providerId":"github","signinUrl":"/auth/github"}')
    ).toBe(true);
  });

  it('returns true for an auth_completed frame', () => {
    expect(isAuthEventPayload('{"$type":"auth_completed","providerId":"github"}')).toBe(true);
  });

  it('returns true for an auth_denied frame', () => {
    expect(isAuthEventPayload('{"$type":"auth_denied","providerId":"github","reason":"timeout"}')).toBe(true);
  });

  it('returns false for the done sentinel', () => {
    expect(isAuthEventPayload('{"$type":"done"}')).toBe(false);
  });

  it('returns false for an error frame', () => {
    expect(isAuthEventPayload('{"$type":"error","code":"provider_unavailable","message":"x"}')).toBe(false);
  });

  it('returns false for a normal text message', () => {
    expect(isAuthEventPayload('{"$type":"text","text":"hello"}')).toBe(false);
  });

  it('returns false for an empty object', () => {
    expect(isAuthEventPayload('{}')).toBe(false);
  });
});

/**
 * The provider-id namespace is the kind discriminator that routes the banner CTA (OAuth sign-in vs
 * the Egress Auth dialog). A mis-classification sends the user to the wrong action.
 */
describe('isPredefinedKeyProvider', () => {
  it('is true for a predefined-<id> provider id', () => {
    expect(isPredefinedKeyProvider('predefined-abc123')).toBe(true);
  });

  it('is false for a managed OAuth provider id', () => {
    expect(isPredefinedKeyProvider('github')).toBe(false);
    expect(isPredefinedKeyProvider('ado')).toBe(false);
    expect(isPredefinedKeyProvider('m365')).toBe(false);
  });
});
