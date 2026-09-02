import { describe, it, expect } from 'vitest';
import { buildDisplayItems, type DisplayableMessage } from '@/composables/messageDisplay';
import { MessageType, type ReasoningMessage } from '@/types';

/**
 * Reasoning pill buffering (#709).
 *
 * A Claude adaptive-thinking model whose request left `thinking.display` at its "omitted" default
 * returns a thinking block with EMPTY text plus a signature, and the backend persists that as a
 * Plain ReasoningMessage with `reasoning: ''`. Buffering it produced a thinking pill with nothing
 * to show, on every generation. The request-shape fix stops new conversations producing those, but
 * already-persisted history still carries them, so the client must not render them either way.
 */
describe('buildDisplayItems reasoning pills', () => {
  function reasoningMessage(reasoning: string, visibility: 0 | 1 | 2): DisplayableMessage {
    const content: ReasoningMessage = {
      $type: MessageType.Reasoning,
      role: 'assistant',
      reasoning,
      visibility,
      generationId: 'gen-1',
    };
    return {
      id: `msg-${reasoning.length}-${visibility}`,
      role: 'assistant',
      status: 'completed',
      content,
      timestamp: 0,
    };
  }

  it('renders a pill for plain reasoning that has text', () => {
    const items = buildDisplayItems([reasoningMessage('Breaking it down: 17 x 23 = 391.', 0)]);

    expect(items.filter((item) => item.type === 'pill')).toHaveLength(1);
  });

  it('renders no pill for plain reasoning with empty text', () => {
    const items = buildDisplayItems([reasoningMessage('', 0)]);

    expect(items).toHaveLength(0);
  });

  it('renders no pill for plain reasoning that is only whitespace', () => {
    const items = buildDisplayItems([reasoningMessage('   \n  ', 0)]);

    expect(items).toHaveLength(0);
  });

  it('renders no pill for empty summary reasoning', () => {
    // The blank-reasoning guard sits after the Encrypted skip, so it covers Summary too — a GPT
    // reasoning summary that came back empty buys no pill either.
    expect(buildDisplayItems([reasoningMessage('', 1)])).toHaveLength(0);
  });

  it('renders a pill for summary reasoning that has text', () => {
    expect(buildDisplayItems([reasoningMessage('Considering the options.', 1)])).toHaveLength(1);
  });

  it('renders no pill for encrypted reasoning', () => {
    // Pre-existing behaviour: the signature blob is noise, never a pill.
    const items = buildDisplayItems([reasoningMessage('ErUBCkYIBxgCKkD3', 2)]);

    expect(items).toHaveLength(0);
  });

  it('keeps a real thinking pill when an empty sibling accompanies it', () => {
    // The empty Plain message and the signature must drop out without taking the readable
    // reasoning of the same generation with them.
    const items = buildDisplayItems([
      reasoningMessage('', 0),
      reasoningMessage('Breaking it down: 17 x 23 = 391.', 0),
      reasoningMessage('ErUBCkYIBxgCKkD3', 2),
    ]);

    const pills = items.filter((item) => item.type === 'pill');
    expect(pills).toHaveLength(1);
    expect(pills[0].items).toHaveLength(1);
  });
});
