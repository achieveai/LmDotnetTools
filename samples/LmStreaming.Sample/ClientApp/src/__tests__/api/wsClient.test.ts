import { describe, expect, it } from 'vitest';
import { normalizeKeys } from '@/api/wsClient';

// BLOCKER 3: tool-call wire JSON uses snake_case identity fields (e.g. `generation_id`). The merge
// key reads camelCase `generationId`, so without a snake_case alias these messages fall back to
// 'default' and fail to group with their camelCase siblings. normalizeKeys must alias the snake_case
// identity fields → camelCase at the deserialize boundary so all downstream consumers see one shape.
describe('normalizeKeys snake_case identity aliasing (BLOCKER 3)', () => {
  it('aliases snake_case generation_id -> generationId', () => {
    const out = normalizeKeys({
      $type: 'tool_call',
      generation_id: 'gen-1',
      run_id: 'run-1',
      parent_run_id: 'parent-1',
      message_order_idx: 2,
      tool_call_id: 'call_1',
    }) as Record<string, unknown>;

    expect(out.generationId).toBe('gen-1');
    expect(out.runId).toBe('run-1');
    expect(out.parentRunId).toBe('parent-1');
    expect(out.messageOrderIdx).toBe(2);
    // tool_call_id is already consumed directly by the merge key; keep it intact.
    expect(out.tool_call_id).toBe('call_1');
  });

  it('still aliases PascalCase keys and does not clobber existing camelCase', () => {
    const out = normalizeKeys({
      GenerationId: 'gen-pascal',
      generation_id: 'gen-snake',
    }) as Record<string, unknown>;

    // An explicit camelCase wins; aliases are write-once and must not overwrite it.
    expect(out.generationId).toBe('gen-pascal');
  });

  it('recurses into nested objects and arrays', () => {
    const out = normalizeKeys({
      tool_calls: [{ generation_id: 'g', tool_call_id: 'c' }],
    }) as { tool_calls: Array<Record<string, unknown>> };

    expect(out.tool_calls[0].generationId).toBe('g');
  });
});
