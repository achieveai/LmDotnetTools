import { describe, it, expect } from 'vitest';
import {
  MessageType,
  isCompactionCheckpointMessage,
  type CompactionCheckpointMessage,
  type IMessage,
} from '@/types';
import { getMergeKey } from '@/composables/messageMergeKey';

/**
 * The checkpoint `$type` is VERSIONED (spec 679 §8.3).
 *
 * The server gives each checkpoint schema version its own discriminator — `compaction_checkpoint`,
 * then `compaction_checkpoint@2` — so that a .NET reader older than a row raises the unknown-type
 * error and skips it, rather than adopting a manifest whose newer sections it silently dropped.
 *
 * That is the right answer for a reader that feeds a model. It is the wrong answer for this client,
 * which renders a divider out of `checkpoint_id` and `narrative` — fields every version carries. When
 * the discriminator moved to `@2`, an exact-match guard stopped recognising the row: the divider
 * disappeared from the transcript and the row fell through to the plain-message path, where it
 * serializes with role `user` and would have been drawn as something the human typed. These tests pin
 * the family match, in both directions.
 */
describe('isCompactionCheckpointMessage across schema versions', () => {
  function checkpoint(type: string): CompactionCheckpointMessage {
    return {
      $type: type,
      role: 'user',
      checkpoint_id: 'cp-t1-1',
      boundary: { seq: 12, message_id: 'm-12' },
      trigger: 'Preemptive',
      manifest: {},
      narrative: 'Turn one gathered the data.',
    } as unknown as CompactionCheckpointMessage;
  }

  it('recognises the schema 1 rows already on disk', () => {
    expect(MessageType.CompactionCheckpoint).toBe('compaction_checkpoint');
    expect(isCompactionCheckpointMessage(checkpoint(MessageType.CompactionCheckpoint))).toBe(true);
  });

  it('recognises the schema 2 rows this server writes', () => {
    expect(MessageType.CompactionCheckpointV2).toBe('compaction_checkpoint@2');
    expect(isCompactionCheckpointMessage(checkpoint(MessageType.CompactionCheckpointV2))).toBe(true);
  });

  it('still recognises a version newer than this client, because a missing divider is the worse failure', () => {
    // The whole reason the guard matches a family rather than a list. A client that ships today and a
    // server that bumps to schema 3 tomorrow must not silently lose the divider.
    expect(isCompactionCheckpointMessage(checkpoint('compaction_checkpoint@3'))).toBe(true);
  });

  it('does not match a different message type that merely starts the same way', () => {
    // Non-vacuity: a guard that said true to everything would pass every test above.
    expect(isCompactionCheckpointMessage(checkpoint('compaction_status'))).toBe(false);
    expect(isCompactionCheckpointMessage(checkpoint('compaction_checkpoint_v9'))).toBe(false);
    expect(isCompactionCheckpointMessage({ $type: 'text', role: 'user' } as unknown as IMessage)).toBe(false);
  });

  it('merges a v1 and a v2 row by checkpoint_id, not by discriminator', () => {
    // The merge key is how a live checkpoint and its reloaded self become one row. Keying off the
    // discriminator would have made the same checkpoint appear twice across a schema bump.
    expect(getMergeKey(checkpoint(MessageType.CompactionCheckpointV2))).toBe(
      getMergeKey(checkpoint(MessageType.CompactionCheckpoint))
    );
  });
});
