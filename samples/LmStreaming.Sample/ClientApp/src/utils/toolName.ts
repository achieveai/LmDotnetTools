/**
 * Wire tool-name normalization + renderer lookup (#199).
 *
 * Backends label the same logical tool inconsistently: sandbox-mounted tools arrive `sandbox-`
 * prefixed and casing varies (`Bash`, `bash`, `SANDBOX-bash`). {@link resolveRenderer} folds those
 * to a single canonical key and resolves the matching {@link ToolRenderer}, falling back to the
 * shared {@link genericRenderer} for anything unknown or empty.
 */
import type { ToolRenderer } from '@/utils/toolTypes';
import { getRegistry, genericRenderer } from '@/components/tools/registry';

/** Leading `sandbox-` prefix, matched case-insensitively. */
const SANDBOX_PREFIX = /^sandbox-/i;

/**
 * Canonical registry key for a wire tool name: strip a leading `sandbox-` prefix (case-insensitive)
 * then lowercase. Returns '' for nullish/empty input.
 */
export function normalizeToolName(wireName: string | null | undefined): string {
  if (!wireName) return '';
  return wireName.replace(SANDBOX_PREFIX, '').toLowerCase();
}

/**
 * Resolve the renderer for a wire tool name. Unknown, empty, or nullish names resolve to the
 * shared {@link genericRenderer}. Never throws.
 */
export function resolveRenderer(wireName: string | null | undefined): ToolRenderer {
  const normalized = normalizeToolName(wireName);
  if (!normalized) return genericRenderer;
  return getRegistry().get(normalized) ?? genericRenderer;
}
