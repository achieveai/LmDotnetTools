import { describe, it, expect } from 'vitest';
import { TodoStatus, type TodoTask } from '@/types/todo';
import {
  artifactFileName,
  countTodoTasks,
  findActiveTaskId,
  flattenTodoTasks,
  isMarkdownArtifact,
  latestNote,
  normalizeTodoTasks,
} from '@/utils/todoBoard';

/**
 * Pure derivations behind the ToDo board (#583, PR 3). These matter more than they look: the panel
 * and the composable BOTH read these functions, so a wrong count here is a wrong count in two
 * places that agree with each other.
 */

function task(id: string, overrides: Partial<TodoTask> = {}): TodoTask {
  return {
    id,
    status: TodoStatus.NotStarted,
    title: `Task ${id}`,
    notes: [],
    artifacts: [],
    subTasks: [],
    ...overrides,
  };
}

describe('normalizeTodoTasks — the wire contract', () => {
  it('parses the documented GET /todos payload shape verbatim', () => {
    // This object is the contract from the design doc: dotted id, enum NAME for status, notes and
    // subTasks always present. If PR 1 ships a different shape, THIS is the assertion that fails.
    const wire = [
      {
        id: '1',
        status: 'InProgress',
        title: 'Wire the SSE endpoint',
        notes: ['waiting on schema'],
        subTasks: [
          { id: '1.1', status: 'Completed', title: 'Add the map', notes: [], subTasks: [] },
        ],
      },
    ];

    expect(normalizeTodoTasks(wire)).toEqual([
      {
        id: '1',
        status: 'InProgress',
        title: 'Wire the SSE endpoint',
        notes: ['waiting on schema'],
        artifacts: [],
        subTasks: [
          {
            id: '1.1',
            status: 'Completed',
            title: 'Add the map',
            notes: [],
            artifacts: [],
            subTasks: [],
          },
        ],
      },
    ]);
  });

  it('accepts every documented status name', () => {
    const wire = ['NotStarted', 'InProgress', 'Completed', 'Removed'].map((status, i) => ({
      id: String(i),
      status,
      title: 't',
      notes: [],
      subTasks: [],
    }));
    expect(normalizeTodoTasks(wire).map((t) => t.status)).toEqual([
      'NotStarted',
      'InProgress',
      'Completed',
      'Removed',
    ]);
  });

  it('resolves an UNKNOWN status to NotStarted, never to Completed', () => {
    // The failure direction is the point: a mis-parsed board may under-report progress, but it must
    // never claim work is finished that is not. PR 4's `Blocked` reaches an older client this way.
    const rows = normalizeTodoTasks([
      { id: '1', status: 'Blocked', title: 'a', notes: [], subTasks: [] },
      { id: '2', status: 42, title: 'b', notes: [], subTasks: [] },
      { id: '3', title: 'c', notes: [], subTasks: [] },
    ]);
    expect(rows.map((r) => r.status)).toEqual(['NotStarted', 'NotStarted', 'NotStarted']);
  });

  it('is case-insensitive on the status name', () => {
    const rows = normalizeTodoTasks([{ id: '1', status: 'inprogress', title: 'a' }]);
    expect(rows[0].status).toBe(TodoStatus.InProgress);
  });

  it('degrades a malformed payload to fewer rows rather than throwing', () => {
    // The board is an accessory to the chat; a payload it cannot read must never take the view down.
    expect(normalizeTodoTasks(null)).toEqual([]);
    expect(normalizeTodoTasks('nope')).toEqual([]);
    expect(normalizeTodoTasks(undefined)).toEqual([]);
    expect(normalizeTodoTasks([null, 7, 'x'])).toEqual([]);
  });

  it('drops a task with no id or no title — it cannot be keyed or labelled', () => {
    const rows = normalizeTodoTasks([
      { id: '', status: 'NotStarted', title: 'no id' },
      { status: 'NotStarted', title: 'missing id' },
      { id: '2', status: 'NotStarted' },
      { id: '3', status: 'NotStarted', title: 'kept' },
    ]);
    expect(rows.map((r) => r.id)).toEqual(['3']);
  });

  it('fills in absent notes/subTasks and discards non-string notes', () => {
    const rows = normalizeTodoTasks([
      { id: '1', status: 'NotStarted', title: 'a', notes: ['ok', 3, null, ''], subTasks: 'nope' },
    ]);
    expect(rows[0].notes).toEqual(['ok']);
    expect(rows[0].subTasks).toEqual([]);
  });

  it('carries artifacts through intact, and an absent key becomes an empty array (PR 5)', () => {
    // A pre-PR-5 server simply omits the key — the second row proves that lands as [], not
    // undefined, so the panel can always read `row.artifacts.length` without guarding.
    const rows = normalizeTodoTasks([
      {
        id: '1',
        status: 'InProgress',
        title: 'a',
        artifacts: ['docs/spec.md', 'out/report.md'],
      },
      { id: '2', status: 'NotStarted', title: 'b' },
    ]);
    expect(rows[0].artifacts).toEqual(['docs/spec.md', 'out/report.md']);
    expect(rows[1].artifacts).toEqual([]);
  });

  it('drops non-string and empty artifact entries rather than rendering blank chips', () => {
    const rows = normalizeTodoTasks([
      { id: '1', status: 'NotStarted', title: 'a', artifacts: ['docs/spec.md', 7, null, ''] },
      { id: '2', status: 'NotStarted', title: 'b', artifacts: 'nope' },
    ]);
    expect(rows[0].artifacts).toEqual(['docs/spec.md']);
    expect(rows[1].artifacts).toEqual([]);
  });

  it('truncates rather than recursing forever on a cyclic payload', () => {
    const cyclic: Record<string, unknown> = { id: '1', status: 'NotStarted', title: 'loop' };
    cyclic.subTasks = [cyclic];
    expect(() => normalizeTodoTasks([cyclic])).not.toThrow();
    expect(flattenTodoTasks(normalizeTodoTasks([cyclic])).length).toBeLessThanOrEqual(16);
  });
});

describe('flattenTodoTasks', () => {
  it('keeps tree order — parent, then its children — and stamps depth', () => {
    const tree = [
      task('1', { subTasks: [task('1.1'), task('1.2', { subTasks: [task('1.2.1')] })] }),
      task('2'),
    ];
    expect(flattenTodoTasks(tree).map((r) => [r.id, r.depth])).toEqual([
      ['1', 0],
      ['1.1', 1],
      ['1.2', 1],
      ['1.2.1', 2],
      ['2', 0],
    ]);
  });

  it('does not re-sort by status, so a row cannot jump while it is read', () => {
    const tree = [
      task('1', { status: TodoStatus.NotStarted }),
      task('2', { status: TodoStatus.Completed }),
      task('3', { status: TodoStatus.InProgress }),
    ];
    expect(flattenTodoTasks(tree).map((r) => r.id)).toEqual(['1', '2', '3']);
  });
});

describe('countTodoTasks', () => {
  it('counts sub-tasks too — a parent is not a summary of its children', () => {
    const tree = [
      task('1', {
        status: TodoStatus.InProgress,
        subTasks: [task('1.1', { status: TodoStatus.Completed }), task('1.2')],
      }),
      task('2', { status: TodoStatus.Completed }),
    ];
    expect(countTodoTasks(tree)).toEqual({
      done: 2,
      inProgress: 1,
      pending: 1,
      removed: 0,
      total: 4,
    });
  });

  it('excludes removed rows from total, so deleting work cannot inflate progress', () => {
    const tree = [
      task('1', { status: TodoStatus.Completed }),
      task('2', { status: TodoStatus.Removed }),
      task('3', { status: TodoStatus.Removed }),
    ];
    const counts = countTodoTasks(tree);
    expect(counts.total).toBe(1);
    expect(counts.done).toBe(1);
    expect(counts.removed).toBe(2);
  });

  it('reports an all-zero board for an empty list', () => {
    expect(countTodoTasks([])).toEqual({ done: 0, inProgress: 0, pending: 0, removed: 0, total: 0 });
  });
});

describe('findActiveTaskId', () => {
  it('picks the FIRST in-progress task in tree order when several are active', () => {
    // Deterministic by tree order so the viewport does not flip between two active rows per frame.
    const tree = [
      task('1', { subTasks: [task('1.1', { status: TodoStatus.InProgress })] }),
      task('2', { status: TodoStatus.InProgress }),
    ];
    expect(findActiveTaskId(tree)).toBe('1.1');
  });

  it('is null when nothing is active', () => {
    expect(findActiveTaskId([task('1', { status: TodoStatus.Completed })])).toBeNull();
    expect(findActiveTaskId([])).toBeNull();
  });
});

describe('artifactFileName — the chip label (PR 5)', () => {
  it('is the last path segment; the full path stays on the tooltip', () => {
    expect(artifactFileName('docs/todo-board/spec.md')).toBe('spec.md');
    expect(artifactFileName('report.md')).toBe('report.md');
  });

  it('falls back to the raw path when there is no usable segment', () => {
    expect(artifactFileName('')).toBe('');
  });
});

describe('isMarkdownArtifact — which chips open the rendered preview (PR 5)', () => {
  it('matches .md and .markdown, case-insensitively', () => {
    expect(isMarkdownArtifact('docs/spec.md')).toBe(true);
    expect(isMarkdownArtifact('notes.MARKDOWN')).toBe(true);
    expect(isMarkdownArtifact('README.MD')).toBe(true);
  });

  it('rejects everything else, including md-as-substring traps', () => {
    expect(isMarkdownArtifact('src/index.ts')).toBe(false);
    expect(isMarkdownArtifact('spec.md.bak')).toBe(false);
    expect(isMarkdownArtifact('md')).toBe(false);
  });
});

describe('latestNote', () => {
  it('returns the last note, which is the current status line', () => {
    expect(latestNote(task('1', { notes: ['old', 'newest'] }))).toBe('newest');
  });

  it('is null when the task has no notes', () => {
    expect(latestNote(task('1'))).toBeNull();
  });
});
