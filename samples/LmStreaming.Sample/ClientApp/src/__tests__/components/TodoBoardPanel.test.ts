import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { mount } from '@vue/test-utils';
import { nextTick } from 'vue';
import fs from 'fs';
import path from 'path';
import TodoBoardPanel from '@/components/TodoBoardPanel.vue';
import { TodoStatus, type TodoTask } from '@/types/todo';
import { normalizeTodoTasks } from '@/utils/todoBoard';

/**
 * The panel is stateless/presentational, exactly like SubAgentListPanel: it takes the board as a
 * prop and derives rows and tiles through the shared pure helpers. ChatLayout decides whether to
 * mount it at all (`v-if="hasTodoBoard"`), so "no board" is not a state this component renders.
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

function mountPanel(tasks: TodoTask[] = []) {
  return mount(TodoBoardPanel, { props: { tasks }, attachTo: document.body });
}

/** A board with one of each live status, plus a sub-task, plus a removed row. */
function sampleBoard(): TodoTask[] {
  return [
    task('1', {
      status: TodoStatus.InProgress,
      notes: ['first note', 'waiting on schema'],
      subTasks: [task('1.1', { status: TodoStatus.Completed, title: 'Add the map' })],
    }),
    task('2', { status: TodoStatus.NotStarted, title: 'Renderer registry' }),
    task('3', { status: TodoStatus.Removed, title: 'Dropped idea' }),
  ];
}

describe('TodoBoardPanel — shape', () => {
  it('is expanded by default: it only mounts when there IS work to watch', async () => {
    const wrapper = mountPanel(sampleBoard());
    expect(wrapper.find('[data-testid="todo-panel"]').exists()).toBe(true);
    expect(wrapper.get('[data-testid="todo-panel-toggle"]').text()).toContain('Work (1/3)');
  });

  it('collapses to the rail on toggle, keeping the progress figure visible', async () => {
    const wrapper = mountPanel(sampleBoard());
    await wrapper.get('[data-testid="todo-panel-toggle"]').trigger('click');

    expect(wrapper.find('[data-testid="todo-panel"]').exists()).toBe(false);
    expect(wrapper.get('[data-testid="todo-panel-toggle"]').text()).toContain('1/3');
  });
});

describe('TodoBoardPanel — summary tiles', () => {
  it('shows done / active / todo counts, removed excluded from all three', () => {
    const wrapper = mountPanel(sampleBoard());
    expect(wrapper.get('[data-testid="todo-tile-completed"]').text()).toContain('1');
    expect(wrapper.get('[data-testid="todo-tile-in-progress"]').text()).toContain('1');
    expect(wrapper.get('[data-testid="todo-tile-not-started"]').text()).toContain('1');
  });

  it('publishes the progress percentage over live tasks only', () => {
    const wrapper = mountPanel([
      task('1', { status: TodoStatus.Completed }),
      task('2', { status: TodoStatus.Completed }),
      task('3', { status: TodoStatus.NotStarted }),
      task('4', { status: TodoStatus.Removed }),
    ]);
    // 2 done of 3 live = 67%. The removed row cannot inflate it.
    expect(wrapper.get('[data-testid="todo-progress"]').attributes('data-percent')).toBe('67');
  });

  it('does not divide by zero when every task is removed', () => {
    const wrapper = mountPanel([task('1', { status: TodoStatus.Removed })]);
    expect(wrapper.get('[data-testid="todo-progress"]').attributes('data-percent')).toBe('0');
  });
});

describe('TodoBoardPanel — states', () => {
  it('says "No active tasks." when every row on the board is Removed — tasks DO exist (584)', () => {
    // The ONLY reachable empty case: ChatLayout's mount gate counts ALL tasks, this list shows only
    // the live ones, so a board of nothing but removed rows mounts and has no live row to show.
    // "No tasks yet." was a lie here — the removed accordion sits right below this line.
    const wrapper = mountPanel([task('1', { status: TodoStatus.Removed })]);
    expect(wrapper.get('[data-testid="todo-empty"]').text()).toContain('No active tasks.');
    expect(wrapper.find('[data-testid="todo-list"]').exists()).toBe(false);
    // The struck-out work stays reachable next to the message that implies it exists.
    expect(wrapper.get('[data-testid="todo-removed-accordion"]').text()).toContain('1 removed');
  });

  it('has no loading state at all — the panel is unmounted for every load it could report', () => {
    // Guards the F-002 decision. ChatLayout mounts on `tasks.length > 0`, and the only load path
    // resets tasks first, so a loading branch here could never render in the composed app. If one
    // is ever reintroduced, wire the mount gate to match it or this reads as coverage that is not.
    const wrapper = mountPanel(sampleBoard());
    expect(wrapper.find('[data-testid="todo-loading"]').exists()).toBe(false);
  });
});

describe('TodoBoardPanel — rows', () => {
  it('renders one row per live task in tree order, publishing id and status', () => {
    const wrapper = mountPanel(sampleBoard());
    const rows = wrapper.findAll('[data-testid="todo-row"]');

    expect(rows.map((r) => r.attributes('data-task-id'))).toEqual(['1', '1.1', '2']);
    expect(rows.map((r) => r.attributes('data-status'))).toEqual([
      'InProgress',
      'Completed',
      'NotStarted',
    ]);
  });

  it('keeps removed rows OUT of the main list', () => {
    const wrapper = mountPanel(sampleBoard());
    const ids = wrapper
      .findAll('[data-testid="todo-list"] [data-testid="todo-row"]')
      .map((r) => r.attributes('data-task-id'));
    expect(ids).not.toContain('3');
  });

  it('renders a short status pill per row', () => {
    const wrapper = mountPanel(sampleBoard());
    expect(wrapper.findAll('[data-testid="todo-row-pill"]').map((p) => p.text())).toEqual([
      'active',
      'done',
      'todo',
    ]);
  });

  it('indents a sub-task by its depth', () => {
    const wrapper = mountPanel(sampleBoard());
    const lines = wrapper.findAll('[data-testid="todo-row"] .todo-line');
    expect(lines[0].attributes('style')).toBeUndefined();
    expect(lines[1].attributes('style')).toContain('padding-left: 28px');
  });

  it('marks the active row', () => {
    const wrapper = mountPanel(sampleBoard());
    const rows = wrapper.findAll('[data-testid="todo-row"]');
    expect(rows[0].classes()).toContain('active');
    expect(rows[2].classes()).not.toContain('active');
  });

  it('renders a Blocked row as "blocked", not as the "todo" fallback (#594 D4)', () => {
    // Before the union carried Blocked, coerceStatus mapped the wire's "Blocked" to NotStarted and
    // the row silently read as ordinary todo work. The wire carries the status name only — no
    // blockedBy list crosses it — so the pill is the whole render.
    const wrapper = mountPanel([task('1', { status: TodoStatus.Blocked, title: 'Waits on 2' })]);
    const row = wrapper.get('[data-testid="todo-row"]');
    expect(row.attributes('data-status')).toBe('Blocked');
    expect(row.get('[data-testid="todo-row-pill"]').text()).toBe('blocked');
    // Blocked is live work: it renders in the main list, never behind the removed accordion.
    expect(wrapper.find('[data-testid="todo-removed-accordion"]').exists()).toBe(false);
  });
});

describe('TodoBoardPanel — the note sub-line', () => {
  it('shows the LATEST note, and only on the active row', () => {
    // Every row carrying its note would turn a glanceable board into a wall of text.
    const wrapper = mountPanel([
      task('1', { status: TodoStatus.InProgress, notes: ['first note', 'waiting on schema'] }),
      task('2', { status: TodoStatus.NotStarted, notes: ['a note nobody asked for'] }),
    ]);

    const notes = wrapper.findAll('[data-testid="todo-row-note"]');
    expect(notes).toHaveLength(1);
    expect(notes[0].text()).toBe('waiting on schema');
  });

  it('renders no note sub-line when the active task has none', () => {
    const wrapper = mountPanel([task('1', { status: TodoStatus.InProgress })]);
    expect(wrapper.find('[data-testid="todo-row-note"]').exists()).toBe(false);
  });
});

describe('TodoBoardPanel — artifact chips (PR 5)', () => {
  it('renders one chip per artifact, labelled by file name with the full path on the tooltip', () => {
    const wrapper = mountPanel([
      task('1', {
        status: TodoStatus.InProgress,
        artifacts: ['docs/todo-board/spec.md', 'out/report.md'],
      }),
      task('2'),
    ]);

    const chips = wrapper.findAll('[data-testid="todo-artifact-chip"]');
    expect(
      chips.map((c) => c.get('[data-testid="todo-artifact-name"]').text())
    ).toEqual(['spec.md', 'report.md']);
    expect(chips.map((c) => c.attributes('data-artifact-path'))).toEqual([
      'docs/todo-board/spec.md',
      'out/report.md',
    ]);
    expect(chips[0].attributes('title')).toBe('docs/todo-board/spec.md');
  });

  it('emits openArtifact with the FULL workspace-relative path, not the chip label', async () => {
    // The modal fetches by path; the shortened label would 404 for anything in a sub-directory.
    const wrapper = mountPanel([
      task('1', { status: TodoStatus.InProgress, artifacts: ['docs/todo-board/spec.md'] }),
    ]);

    await wrapper.get('[data-testid="todo-artifact-chip"]').trigger('click');

    expect(wrapper.emitted('openArtifact')).toEqual([['docs/todo-board/spec.md']]);
  });

  it('renders no chip strip at all for a task without artifacts', () => {
    const wrapper = mountPanel(sampleBoard());
    expect(wrapper.find('[data-testid="todo-artifact-chip"]').exists()).toBe(false);
  });
});

/** One 20-deep single chain in the wire shape, run through the REAL normalizer — the same
 *  pipeline useTodoBoard feeds this panel, so these tests exercise the guard, not a hand-built
 *  imitation of its output. */
function deepBoard(depth: number): TodoTask[] {
  const root: Record<string, unknown> = {
    id: '1',
    status: 'NotStarted',
    title: 'Level 1',
    subTasks: [],
  };
  let current = root;
  let id = '1';
  for (let level = 2; level <= depth; level++) {
    id = `${id}.1`;
    const child: Record<string, unknown> = {
      id,
      status: 'NotStarted',
      title: `Level ${level}`,
      subTasks: [],
    };
    (current.subTasks as unknown[]).push(child);
    current = child;
  }
  return normalizeTodoTasks([root]);
}

describe('TodoBoardPanel — deep boards (#608)', () => {
  it('renders the first 16 levels of a 20-deep board plus an explicit truncation row', () => {
    // The server nests without limit; the client's guard depth is stack safety, not policy. What
    // the guard drops must be SAID, not silently missing — "4 deeper tasks not shown" is the
    // difference between a safeguard and a lie about what work exists.
    const wrapper = mountPanel(deepBoard(20));

    expect(wrapper.findAll('[data-testid="todo-row"]')).toHaveLength(16);
    expect(wrapper.get('[data-testid="todo-truncated-row"]').text()).toBe(
      '4 deeper tasks not shown'
    );
  });

  it('uses singular wording for a single dropped task', () => {
    const wrapper = mountPanel(deepBoard(17));
    expect(wrapper.get('[data-testid="todo-truncated-row"]').text()).toBe(
      '1 deeper task not shown'
    );
  });

  it('renders no truncation row at all for a board within the guard depth', () => {
    expect(mountPanel(deepBoard(16)).find('[data-testid="todo-truncated-row"]').exists()).toBe(
      false
    );
    expect(mountPanel(sampleBoard()).find('[data-testid="todo-truncated-row"]').exists()).toBe(
      false
    );
  });

  it('clamps the indent so a depth-15 row starts where a depth-8 row does and stays usable', () => {
    // 14px per level in a 260px panel: unclamped, depth 15 would indent 224px and leave no room
    // for the title. Depth 8 is the last stepped indent; everything deeper shares it.
    const lines = mountPanel(deepBoard(20)).findAll('[data-testid="todo-row"] .todo-line');

    expect(lines[8].attributes('style')).toContain('padding-left: 126px');
    expect(lines[15].attributes('style')).toContain('padding-left: 126px');
    // The clamp must not flatten the REAL hierarchy below it: depth 7 still steps.
    expect(lines[7].attributes('style')).toContain('padding-left: 112px');
  });
});

describe('TodoBoardPanel — removed accordion', () => {
  it('collapses removed rows behind one accordion row, closed by default', () => {
    const wrapper = mountPanel(sampleBoard());
    const accordion = wrapper.get('[data-testid="todo-removed-accordion"]');
    expect(accordion.text()).toContain('1 removed');
    expect(wrapper.find('[data-testid="todo-removed-list"]').exists()).toBe(false);
  });

  it('reveals the removed rows on click', async () => {
    const wrapper = mountPanel(sampleBoard());
    await wrapper.get('[data-testid="todo-removed-accordion"]').trigger('click');

    const removed = wrapper.findAll('[data-testid="todo-removed-list"] [data-testid="todo-row"]');
    expect(removed.map((r) => r.attributes('data-task-id'))).toEqual(['3']);
  });

  it('shows no accordion when nothing was removed', () => {
    const wrapper = mountPanel([task('1')]);
    expect(wrapper.find('[data-testid="todo-removed-accordion"]').exists()).toBe(false);
  });
});

/**
 * Structural (source-text) guards for CSS jsdom cannot lay out, following the
 * `AppShellLayout.test.ts` precedent: jsdom computes no real layout, so whether an ellipsis engages
 * or a colour distinguishes a pill can only be pinned by asserting the declarations exist.
 */
describe('TodoBoardPanel — style guards (source text)', () => {
  const sfc = fs.readFileSync(
    path.resolve(__dirname, '../../components/TodoBoardPanel.vue'),
    'utf-8'
  ) as string;

  it('puts the ellipsis rules on .todo-artifact-name, where text-overflow can actually apply (596/F-007)', () => {
    // `text-overflow` applies to a block container's own inline content, never to a flex
    // container's items — on the inline-flex chip it silently never rendered and long file names
    // just clipped. The name span must carry the trio itself, plus min-width: 0 to be allowed to
    // shrink below its content.
    expect(sfc).toMatch(/\.todo-artifact-name\s*\{[^}]*text-overflow:\s*ellipsis/);
    expect(sfc).toMatch(/\.todo-artifact-name\s*\{[^}]*overflow:\s*hidden/);
    expect(sfc).toMatch(/\.todo-artifact-name\s*\{[^}]*white-space:\s*nowrap/);
    expect(sfc).toMatch(/\.todo-artifact-name\s*\{[^}]*min-width:\s*0/);
    // And the broken form must not come back: ellipsis on the inline-flex chip itself is inert.
    expect(sfc).not.toMatch(/\.todo-artifact-chip\s*\{[^}]*text-overflow/);
  });

  it('gives the Blocked pill its own colour, distinct from the default gray (#594 D4)', () => {
    expect(sfc).toMatch(/\.status-Blocked\s+\.todo-pill\s*\{[^}]*background/);
  });
});

describe('TodoBoardPanel — autoscroll to the active row', () => {
  let scrolled: (string | null)[];
  let original: typeof Element.prototype.scrollIntoView;

  beforeEach(() => {
    scrolled = [];
    original = Element.prototype.scrollIntoView;
    // jsdom does not implement scrollIntoView at all; installing a spy is both the stub and the probe.
    Element.prototype.scrollIntoView = vi.fn(function (this: Element) {
      scrolled.push(this.getAttribute('data-task-id'));
    }) as unknown as typeof Element.prototype.scrollIntoView;
  });

  afterEach(() => {
    Element.prototype.scrollIntoView = original;
    vi.useRealTimers();
  });

  it('scrolls the newly active row into view', async () => {
    const wrapper = mountPanel([
      task('1', { status: TodoStatus.Completed }),
      task('2', { status: TodoStatus.NotStarted }),
    ]);

    await wrapper.setProps({
      tasks: [task('1', { status: TodoStatus.Completed }), task('2', { status: TodoStatus.InProgress })],
    });
    await nextTick();

    expect(scrolled).toEqual(['2']);
  });

  it('does NOT scroll when no task is active', async () => {
    const wrapper = mountPanel([task('1', { status: TodoStatus.InProgress })]);
    scrolled.length = 0;

    await wrapper.setProps({ tasks: [task('1', { status: TodoStatus.Completed })] });
    await nextTick();

    expect(scrolled).toEqual([]);
  });

  it('is suppressed after the reader scrolls the list by hand', async () => {
    // Stealing the viewport back from someone who just scrolled away is the behaviour this prevents.
    const wrapper = mountPanel([
      task('1', { status: TodoStatus.InProgress }),
      task('2', { status: TodoStatus.NotStarted }),
      task('3', { status: TodoStatus.NotStarted }),
    ]);
    await nextTick();
    scrolled.length = 0;

    await wrapper.get('[data-testid="todo-list"]').trigger('scroll');

    await wrapper.setProps({
      tasks: [
        task('1', { status: TodoStatus.Completed }),
        task('2', { status: TodoStatus.NotStarted }),
        task('3', { status: TodoStatus.InProgress }),
      ],
    });
    await nextTick();

    expect(scrolled).toEqual([]);
  });

  it('resumes autoscroll once the manual-scroll suppression window has passed', async () => {
    const wrapper = mountPanel([
      task('1', { status: TodoStatus.InProgress }),
      task('2', { status: TodoStatus.NotStarted }),
    ]);
    await nextTick();
    await wrapper.get('[data-testid="todo-list"]').trigger('scroll');
    scrolled.length = 0;

    // 5s later the reader is assumed to have stopped reading by hand.
    const realNow = Date.now;
    const base = realNow();
    vi.spyOn(Date, 'now').mockImplementation(() => base + 6000);
    try {
      await wrapper.setProps({
        tasks: [task('1', { status: TodoStatus.Completed }), task('2', { status: TodoStatus.InProgress })],
      });
      await nextTick();
    } finally {
      vi.mocked(Date.now).mockRestore();
    }

    expect(scrolled).toEqual(['2']);
  });
});
