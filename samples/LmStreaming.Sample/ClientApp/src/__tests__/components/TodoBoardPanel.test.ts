import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { mount } from '@vue/test-utils';
import { nextTick } from 'vue';
import TodoBoardPanel from '@/components/TodoBoardPanel.vue';
import { TodoStatus, type TodoTask } from '@/types/todo';

/**
 * The panel is stateless/presentational, exactly like SubAgentListPanel: it takes the board as a
 * prop and derives rows and tiles through the shared pure helpers. ChatLayout decides whether to
 * mount it at all (`v-if="hasTodoBoard"`), so "no board" is not a state this component renders.
 */

function task(id: string, overrides: Partial<TodoTask> = {}): TodoTask {
  return { id, status: TodoStatus.NotStarted, title: `Task ${id}`, notes: [], subTasks: [], ...overrides };
}

function mountPanel(tasks: TodoTask[] = [], isLoading = false) {
  return mount(TodoBoardPanel, { props: { tasks, isLoading }, attachTo: document.body });
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
  it('renders a loading line while the first board is in flight', () => {
    const wrapper = mountPanel([], true);
    expect(wrapper.find('[data-testid="todo-loading"]').exists()).toBe(true);
    expect(wrapper.find('[data-testid="todo-empty"]').exists()).toBe(false);
  });

  it('renders an empty line for a board that has no live rows', () => {
    const wrapper = mountPanel([], false);
    expect(wrapper.get('[data-testid="todo-empty"]').text()).toContain('No tasks yet.');
    expect(wrapper.find('[data-testid="todo-list"]').exists()).toBe(false);
  });

  it('keeps showing the rows it already has while a refresh is in flight — no blank flash', () => {
    const wrapper = mountPanel(sampleBoard(), true);
    expect(wrapper.find('[data-testid="todo-loading"]').exists()).toBe(false);
    expect(wrapper.findAll('[data-testid="todo-row"]').length).toBeGreaterThan(0);
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
