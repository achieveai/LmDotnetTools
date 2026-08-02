import { inject } from 'vue';

/** The provider key ChatLayout uses to expose deferred client-tool-result submission (#246). */
export const SUBMIT_CLIENT_TOOL_RESULT = 'submitClientToolResult';

/**
 * Outcome of a submitted client tool result (#246). Mirrors the server's
 * `client_tool_result_ack` / `client_tool_result_error` frames (see `api/wsClient.ts`
 * `sendClientToolResult`). `code` is present only for `status: 'error'`.
 */
export type ClientToolSubmitOutcome =
  | { status: 'acked'; duplicate: boolean }
  | { status: 'error'; code: string; message: string };

export type ClientToolSubmitFn = (
  toolCallId: string,
  result: string,
  isError?: boolean
) => Promise<ClientToolSubmitOutcome>;

/**
 * Inject the function a "rich" tool component (e.g. `QuestionRich.vue`) calls to resolve a
 * deferred client tool call. Falls back to a rejection so a component mounted without a provider
 * (e.g. a bare unit test) fails loudly instead of hanging silently.
 */
export function useClientToolSubmit() {
  const submit = inject<ClientToolSubmitFn>(SUBMIT_CLIENT_TOOL_RESULT, () =>
    Promise.resolve({ status: 'error', code: 'not_connected', message: 'No submit handler provided' })
  );
  return { submit };
}
