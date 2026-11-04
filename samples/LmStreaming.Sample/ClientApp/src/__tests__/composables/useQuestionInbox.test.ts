import { defineComponent } from 'vue';
import { mount } from '@vue/test-utils';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { useQuestionInbox, type QuestionInboxOptions } from '@/composables/useQuestionInbox';
import type {
  PendingQuestionEvent,
  QuestionEventHandlers,
  connectQuestionEvents,
} from '@/api/eventsWsClient';

/**
 * Stand-in for the `/ws/events` stream. Records every connection so a test can drive the latest one
 * through its handlers and count how many were opened and closed.
 */
function eventsStub() {
  const state: { connections: QuestionEventHandlers[]; closed: number } = { connections: [], closed: 0 };
  const events: typeof connectQuestionEvents = (handlers) => {
    state.connections.push(handlers);
    return {
      close: () => {
        state.closed += 1;
      },
    };
  };
  const latest = () => state.connections[state.connections.length - 1]!;
  return { state, events, latest };
}

function render(options: QuestionInboxOptions = {}) {
  const stub = eventsStub();
  let inbox!: ReturnType<typeof useQuestionInbox>;
  const wrapper = mount(
    defineComponent({
      setup() {
        inbox = useQuestionInbox({ events: stub.events, ...options });
        return () => null;
      },
    })
  );
  return { wrapper, inbox, stub };
}

function pushed(overrides: Partial<PendingQuestionEvent> = {}): PendingQuestionEvent {
  return {
    rootThreadId: 'other',
    agentId: null,
    childThreadId: null,
    toolCallId: 'call-1',
    prompt: 'Pushed prompt',
    conversationTitle: 'Conversation other',
    provider: null,
    mode: null,
    workspace: null,
    agentName: null,
    raisedAtUtc: '2026-09-21T10:00:00.0000000Z',
    ...overrides,
  };
}

afterEach(() => vi.restoreAllMocks());

describe('useQuestionInbox', () => {
  /**
   * The regression this composable was rewritten for: it read every conversation's messages and
   * sub-agent roster (thousands of requests, half of them 503) to find questions the server already
   * knew about. Mounting must open the event stream and make no HTTP request at all.
   */
  it('opens the event stream and makes no HTTP request', () => {
    const fetchSpy = vi.spyOn(globalThis, 'fetch');
    const { wrapper, stub } = render();

    expect(stub.state.connections).toHaveLength(1);
    expect(fetchSpy).not.toHaveBeenCalled();
    wrapper.unmount();
  });

  it('lists a snapshot, and a later snapshot replaces it rather than adding to it', () => {
    const onQuestionRaised = vi.fn();
    const { wrapper, inbox, stub } = render({ onQuestionRaised });
    expect(inbox.isRefreshing.value).toBe(true);

    stub.latest().onSnapshot([pushed({ toolCallId: 'a' }), pushed({ toolCallId: 'b' })]);
    expect(inbox.entries.value.map((entry) => entry.toolCallId)).toEqual(['a', 'b']);
    expect(inbox.isRefreshing.value).toBe(false);

    // The server's list is complete, so a question missing from the next snapshot is not waiting.
    stub.latest().onSnapshot([pushed({ toolCallId: 'b' })]);
    expect(inbox.entries.value.map((entry) => entry.toolCallId)).toEqual(['b']);
    // A snapshot is what was already there; only a push is an arrival worth announcing.
    expect(onQuestionRaised).not.toHaveBeenCalled();
    wrapper.unmount();
  });

  it('adds a pushed question once, announcing it once, and removes it when it settles', () => {
    const onQuestionRaised = vi.fn();
    const { wrapper, inbox, stub } = render({ onQuestionRaised });
    stub.latest().onSnapshot([]);

    stub.latest().onPending(pushed());
    stub.latest().onPending(pushed()); // a restart re-raises the same call
    expect(inbox.entries.value).toHaveLength(1);
    expect(onQuestionRaised).toHaveBeenCalledTimes(1);

    stub.latest().onSettled('other', 'call-1');
    expect(inbox.entries.value).toEqual([]);
    wrapper.unmount();
  });

  it('keeps a root question and a sub-agent question on the same call id apart', () => {
    const { wrapper, inbox, stub } = render();
    stub.latest().onSnapshot([
      pushed(),
      pushed({ agentId: 'agent-2', childThreadId: 'child-2', agentName: 'Researcher' }),
      pushed({ agentId: 'agent-3', childThreadId: 'child-3' }),
    ]);

    expect(inbox.entries.value.map((entry) => entry.agentName)).toEqual(['Main agent', 'Researcher', 'agent-3']);
    expect(inbox.entries.value[1]!.childThreadId).toBe('child-2');
    wrapper.unmount();
  });

  it("carries the conversation's bindings so an unlisted conversation opens with them", () => {
    const { wrapper, inbox, stub } = render();
    stub.latest().onPending(
      pushed({ conversationTitle: 'Budget review', provider: 'openai', mode: 'coder', workspace: 'repo' })
    );

    expect(inbox.entries.value[0]!.conversation).toMatchObject({
      threadId: 'other',
      title: 'Budget review',
      provider: 'openai',
      mode: 'coder',
      workspace: 'repo',
    });
    wrapper.unmount();
  });

  it('refresh reconnects for a fresh snapshot instead of reading anything', async () => {
    const fetchSpy = vi.spyOn(globalThis, 'fetch');
    const { wrapper, inbox, stub } = render();
    stub.latest().onSnapshot([pushed()]);

    await inbox.refresh();
    expect(stub.state.closed).toBe(1);
    expect(stub.state.connections).toHaveLength(2);
    expect(inbox.isRefreshing.value).toBe(true);
    // The old list stays on screen until the new snapshot replaces it.
    expect(inbox.entries.value).toHaveLength(1);

    stub.latest().onSnapshot([]);
    expect(inbox.entries.value).toEqual([]);
    expect(fetchSpy).not.toHaveBeenCalled();
    wrapper.unmount();
  });

  it('says so while the stream is down, and clears it when it is back', () => {
    const { wrapper, inbox, stub } = render();
    stub.latest().onConnectionChange?.(false);
    expect(inbox.error.value).toMatch(/reconnecting/);

    stub.latest().onConnectionChange?.(true);
    expect(inbox.error.value).toBeNull();
    wrapper.unmount();
  });

  it('closes the event stream on dispose', () => {
    const { wrapper, stub } = render();
    wrapper.unmount();
    expect(stub.state.closed).toBe(1);
  });
});
