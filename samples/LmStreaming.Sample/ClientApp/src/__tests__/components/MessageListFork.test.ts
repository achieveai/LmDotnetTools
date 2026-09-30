import { describe, it, expect } from 'vitest';
import { mount } from '@vue/test-utils';
import MessageList from '@/components/MessageList.vue';
import type { DisplayItem } from '@/types';
import { MessageType } from '@/types';
import type { BranchPoint } from '@/api/conversationsApi';

(globalThis as any).ResizeObserver = class ResizeObserver {
  observe() {}
  unobserve() {}
  disconnect() {}
};

const text = (role: 'user' | 'assistant', value: string, isThinking = false) => ({
  $type: MessageType.Text,
  role,
  text: value,
  isThinking,
});

/** Two completed runs. Run r1 has two answer bubbles (a tool call between them) and a thinking bubble. */
const twoRuns = (): DisplayItem[] => [
  { type: 'user-message', id: 'u1', content: text('user', 'First'), status: 'completed', timestamp: 1, persistedId: 'pm-u1', seq: 1 },
  { type: 'assistant-message', id: 'a1-think', content: text('assistant', 'hmm', true), runId: 'r1', seq: 2 },
  { type: 'assistant-message', id: 'a1-early', content: text('assistant', 'Looking'), runId: 'r1', seq: 3 },
  { type: 'assistant-message', id: 'a1-last', content: text('assistant', 'Done'), runId: 'r1', seq: 5 },
  // A live user message whose stored id is not known yet.
  { type: 'user-message', id: 'input-2', content: text('user', 'Second'), status: 'completed', timestamp: 2 },
  { type: 'assistant-message', id: 'a2-last', content: text('assistant', 'Again'), runId: 'r2' },
];

function mountList(props: {
  displayItems?: DisplayItem[];
  isLoading?: boolean;
  forkActions?: boolean;
  branchPoints?: BranchPoint[];
}) {
  return mount(MessageList, {
    props: { displayItems: twoRuns(), forkActions: true, ...props },
  });
}

/** The answer bubble text next to each "Fork from here" button. */
function forkButtonBubbles(wrapper: ReturnType<typeof mountList>): string[] {
  return wrapper
    .findAll('[data-testid="fork-from-here-button"]')
    .map((button) => button.element.closest('.text-bubble-row')!.querySelector('[data-testid="assistant-text"]')!.textContent!.trim());
}

describe('MessageList fork actions', () => {
  it('offers "Fork from here" only on the last answer bubble of each completed run', () => {
    const wrapper = mountList({});

    expect(forkButtonBubbles(wrapper)).toEqual(['Done', 'Again']);
  });

  it('hides both fork buttons while a run streams', () => {
    const wrapper = mountList({ isLoading: true });

    expect(wrapper.find('[data-testid="fork-from-here-button"]').exists()).toBe(false);
    expect(wrapper.find('[data-testid="edit-in-fork-button"]').exists()).toBe(false);
  });

  it('hides both fork buttons when fork actions are off (CLI-backed provider)', () => {
    const wrapper = mountList({ forkActions: false });

    expect(wrapper.find('[data-testid="fork-from-here-button"]').exists()).toBe(false);
    expect(wrapper.find('[data-testid="edit-in-fork-button"]').exists()).toBe(false);
  });

  it('offers "Edit in fork" only on user messages with a stored id', () => {
    const wrapper = mountList({});

    const buttons = wrapper.findAll('[data-testid="edit-in-fork-button"]');
    expect(buttons).toHaveLength(1);
    expect(buttons[0].element.closest('[data-testid="user-message-group"]')!.getAttribute('data-message-id')).toBe('u1');
  });

  it('emits the run id for "Fork from here" and the stored id for "Edit in fork"', async () => {
    const wrapper = mountList({});

    await wrapper.findAll('[data-testid="fork-from-here-button"]')[0].trigger('click');
    await wrapper.get('[data-testid="edit-in-fork-button"]').trigger('click');

    expect(wrapper.emitted('forkAfterRun')).toEqual([['r1']]);
    expect(wrapper.emitted('editInFork')).toEqual([['pm-u1']]);
  });

  it('renders the branch switcher after the message of its fork point and relays arrow clicks', async () => {
    const point: BranchPoint = {
      afterMessageId: 'pm-4-tool-result',
      afterSeq: 4,
      options: [
        { threadId: 'src', title: 'Original', current: true },
        { threadId: 'fork-1', title: 'Try B', current: false },
      ],
    };
    const wrapper = mountList({ branchPoints: [point] });

    const switcher = wrapper.get('[data-testid="branch-switcher"]');
    // Seq 4 is not drawn; the last drawn item at or before it is the "Looking" bubble (seq 3).
    const previousBubble = switcher.element.previousElementSibling!;
    expect(previousBubble.querySelector('[data-testid="assistant-text"]')!.textContent!.trim()).toBe('Looking');

    await switcher.get('[data-testid="branch-switcher-next"]').trigger('click');
    expect(wrapper.emitted('openBranch')).toEqual([['fork-1']]);
  });
});
