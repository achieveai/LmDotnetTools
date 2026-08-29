/**
 * Pure derivations over a ToDo board (#583, PR 3). No Vue imports, so both the composable and the
 * panel component read the SAME functions rather than each deriving counts their own way — the
 * tile numbers and the rendered rows cannot drift apart if there is only one implementation.
 *
 * Mirrors the `utils/agentColors.ts` precedent: pure module, cheap to unit-test directly.
 */
import { TodoStatus, type TodoStatusValue, type TodoTask } from '@/types/todo';

/**
 * Depth cap for the recursive walks. The server builds this tree from dotted ids so it cannot
 * legally nest this deep; the cap exists so a malformed or cyclic payload truncates instead of
 * blowing the stack and taking the whole chat view down with it.
 */
const MAX_DEPTH = 16;

const STATUS_BY_LOWER_NAME: ReadonlyMap<string, TodoStatusValue> = new Map(
  Object.values(TodoStatus).map((s) => [s.toLowerCase(), s])
);

/** Glyph per status. Glyph AND colour both carry the state, so colour is never the only signal. */
export const TODO_STATUS_GLYPH: Record<TodoStatusValue, string> = {
  [TodoStatus.NotStarted]: '○',
  [TodoStatus.InProgress]: '▶',
  [TodoStatus.Completed]: '✓',
  [TodoStatus.Removed]: '~',
};

/** Short pill label per status. Kept to one word: the panel is scanned, not read. */
export const TODO_STATUS_LABEL: Record<TodoStatusValue, string> = {
  [TodoStatus.NotStarted]: 'todo',
  [TodoStatus.InProgress]: 'active',
  [TodoStatus.Completed]: 'done',
  [TodoStatus.Removed]: 'removed',
};

/**
 * Coerces one wire value to a known status.
 *
 * An UNRECOGNIZED status resolves to `NotStarted`, never to `Completed`. That direction is
 * deliberate: the failure mode of a mis-parse is then a board that under-reports progress, which
 * looks like work outstanding, rather than one that claims work is finished when it is not. When
 * PR 4 adds `Blocked`, a client running ahead of its server sees those rows as `todo` — visible and
 * honest — instead of dropping them.
 */
function coerceStatus(raw: unknown): TodoStatusValue {
  if (typeof raw !== 'string') return TodoStatus.NotStarted;
  return STATUS_BY_LOWER_NAME.get(raw.trim().toLowerCase()) ?? TodoStatus.NotStarted;
}

function coerceNotes(raw: unknown): string[] {
  if (!Array.isArray(raw)) return [];
  return raw.filter((n): n is string => typeof n === 'string' && n.length > 0);
}

/**
 * Same tolerance as notes: string entries only, empties dropped. A payload from a pre-PR-5 server
 * simply has no `artifacts` key, which lands here as `undefined` and comes back `[]`.
 */
function coerceArtifacts(raw: unknown): string[] {
  if (!Array.isArray(raw)) return [];
  return raw.filter((a): a is string => typeof a === 'string' && a.length > 0);
}

/** The chip label: the path's last segment. The full path stays on the chip's tooltip. */
export function artifactFileName(path: string): string {
  const segments = path.split('/').filter(Boolean);
  return segments.length > 0 ? segments[segments.length - 1] : path;
}

/** Whether a chip opens the markdown preview modal (vs. the plain-text branch inside it). */
export function isMarkdownArtifact(path: string): boolean {
  return /\.(md|markdown)$/i.test(path);
}

/**
 * Tolerantly parses whatever the endpoint or the push frame handed us into `TodoTask[]`.
 *
 * The board is a read-only accessory to the chat: a payload this cannot understand must degrade to
 * fewer rows, never to a thrown error that takes out the surrounding view. A task missing an `id`
 * or a `title` is dropped (it cannot be keyed or labelled); everything else is filled in.
 */
export function normalizeTodoTasks(raw: unknown, depth = 0): TodoTask[] {
  if (!Array.isArray(raw) || depth >= MAX_DEPTH) return [];

  const out: TodoTask[] = [];
  for (const entry of raw) {
    if (entry == null || typeof entry !== 'object') continue;
    const record = entry as Record<string, unknown>;
    const id = record.id;
    const title = record.title;
    if (typeof id !== 'string' || id.length === 0) continue;
    if (typeof title !== 'string') continue;

    out.push({
      id,
      title,
      status: coerceStatus(record.status),
      notes: coerceNotes(record.notes),
      artifacts: coerceArtifacts(record.artifacts),
      subTasks: normalizeTodoTasks(record.subTasks, depth + 1),
    });
  }
  return out;
}

/** One rendered line: a task plus the nesting depth the panel indents it by. */
export interface TodoRow extends TodoTask {
  depth: number;
}

/**
 * Flattens the tree to the rows the panel renders, in TREE ORDER — parent immediately followed by
 * its children. Order is fixed by construction and never re-sorted by status, so a row cannot jump
 * under the reader's cursor when an agent flips its state mid-scan.
 */
export function flattenTodoTasks(tasks: TodoTask[], depth = 0): TodoRow[] {
  if (depth >= MAX_DEPTH) return [];
  const rows: TodoRow[] = [];
  for (const task of tasks) {
    rows.push({ ...task, depth });
    rows.push(...flattenTodoTasks(task.subTasks, depth + 1));
  }
  return rows;
}

export interface TodoCounts {
  done: number;
  inProgress: number;
  pending: number;
  removed: number;
  /** Live tasks only — `removed` is excluded, so the progress bar cannot be gamed by deleting rows. */
  total: number;
}

/** Counts every task in the tree, sub-tasks included: a parent is not a summary of its children. */
export function countTodoTasks(tasks: TodoTask[]): TodoCounts {
  const counts: TodoCounts = { done: 0, inProgress: 0, pending: 0, removed: 0, total: 0 };
  for (const row of flattenTodoTasks(tasks)) {
    if (row.status === TodoStatus.Removed) {
      counts.removed++;
      continue;
    }
    counts.total++;
    if (row.status === TodoStatus.Completed) counts.done++;
    else if (row.status === TodoStatus.InProgress) counts.inProgress++;
    else counts.pending++;
  }
  return counts;
}

/**
 * The row the panel scrolls to: the FIRST in-progress task in tree order.
 *
 * "First", not "only" — today's `TaskManager` does not enforce one active task, and PR 4's
 * one-active-per-assignee rule still permits several across agents. Picking deterministically by
 * tree order means the viewport does not flip between two active rows on every frame.
 */
export function findActiveTaskId(tasks: TodoTask[]): string | null {
  const active = flattenTodoTasks(tasks).find((r) => r.status === TodoStatus.InProgress);
  return active ? active.id : null;
}

/** The most recent note on a task — the sub-line under the active row. Null when it has none. */
export function latestNote(task: TodoTask): string | null {
  return task.notes.length > 0 ? task.notes[task.notes.length - 1] : null;
}
