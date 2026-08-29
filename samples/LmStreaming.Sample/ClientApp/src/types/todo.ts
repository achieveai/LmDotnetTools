/**
 * Client mirror of the server's ToDo board — PR 3 of the ToDo-board design (#583).
 *
 * These are the CURRENT `TaskManager.TaskItem` fields (shipped in #312) and nothing else:
 * Id / Status / SubTasks / Title / Notes. `Assignee`, `BlockedBy`, `Artifacts` and `Times` arrive
 * with PRs 4-5 and are deliberately absent here — a column the panel cannot populate yet renders as
 * a permanently empty one, which reads as a bug rather than as a feature that has not shipped.
 *
 * The board is read-only in v1. There is no POST; the shapes below are the wire contract for
 * `GET /api/conversations/{threadId}/todos` (PR 1) and the `conversation_todo` push frame (PR 2).
 */

/**
 * Status values are the verbatim C# `TaskStatus` enum NAMES, as serialized by
 * `JsonStringEnumConverter`. PR 4 adds `Blocked`; until then a board can only carry these four.
 */
export const TodoStatus = {
  NotStarted: 'NotStarted',
  InProgress: 'InProgress',
  Completed: 'Completed',
  Removed: 'Removed',
} as const;

export type TodoStatusValue = (typeof TodoStatus)[keyof typeof TodoStatus];

/** One task on the board. `id` is the dotted tree path ("1", "1.2") and doubles as the render key. */
export interface TodoTask {
  id: string;
  status: TodoStatusValue;
  title: string;
  notes: string[];
  subTasks: TodoTask[];
}

/**
 * Body of `GET /api/conversations/{threadId}/todos`, and the payload the `conversation_todo` frame
 * flattens onto itself. A 404 (no board recorded, or a build predating PR 1) is NOT this shape —
 * the API layer maps it to `null`, which the panel renders as "no board", not as an empty board.
 */
export interface TodoBoardSnapshot {
  threadId?: string;
  tasks: TodoTask[];
}
