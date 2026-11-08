import type { ProviderRetryMessage } from '@/types';

/**
 * The one status line shown while the server waits to retry a failed model call (#930 P1 item 1).
 * Says why the turn has gone quiet, how long the wait is, and which attempt comes next.
 */
export function formatProviderRetry(frame: ProviderRetryMessage): string {
  const seconds = Math.max(0, Math.round(frame.delayMs / 1000));
  const reason =
    frame.statusCode === 429
      ? 'is rate limiting requests (429)'
      : frame.statusCode
        ? `returned an error (${frame.statusCode})`
        : 'could not be reached';
  const wait = frame.fromRetryAfter ? `Retrying in ${seconds}s, as the provider asked` : `Retrying in ${seconds}s`;
  return `The model provider ${reason}. ${wait} (attempt ${frame.attempt + 1} of ${frame.maxAttempts}).`;
}
