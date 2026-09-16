/**
 * The single `invalid_env` error type, shared by every API module that can receive one.
 *
 * It lives here rather than in `workspacesApi` or `chatModesApi` because BOTH routes return the same
 * `400 { code: 'invalid_env', keys, layer }` body, and while each module declared its own class the
 * two were unrelated types: `e instanceof InvalidEnvError` imported from one module was always false
 * for an error thrown by the other. Any caller handling both — `ChatLayout` handles exactly that —
 * silently took the generic branch for one of them.
 */

/** Raised on HTTP 400 `invalid_env`: one or more `env` keys are malformed, protected or duplicated. */
export class InvalidEnvError extends Error {
  readonly keys: string[];
  readonly layer: string | null;
  constructor(message: string, keys: string[] = [], layer: string | null = null) {
    super(message);
    this.name = 'InvalidEnvError';
    this.keys = keys;
    this.layer = layer;
  }
}
