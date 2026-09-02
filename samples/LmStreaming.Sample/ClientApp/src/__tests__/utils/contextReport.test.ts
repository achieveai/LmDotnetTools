import { describe, it, expect } from 'vitest';
import type { AgentContextRow, ConversationContextReport, ContextObservation } from '@/types/context';
import type { ContextPressureMessage } from '@/types/messages';
import {
  applyPressureFrame,
  capacityLabel,
  compactionLabel,
  costLabel,
  decisionLabel,
  formatMicros,
  freshnessLabel,
  rowFromWire,
  temperatureLabel,
  utilizationOf,
  viewFromReport,
} from '@/utils/contextReport';

function observation(overrides: Partial<ContextObservation> = {}): ContextObservation {
  return {
    thread_id: 'thread-1',
    agent_id: 'root',
    run_id: 'run-1',
    generation_id: 'gen-2',
    generation_ordinal: 2,
    observed_at_utc: '2026-09-02T10:00:02Z',
    effective_model_id: 'model-x',
    estimated_input_tokens: 4_000,
    measured_input_tokens: 5_000,
    provenance: 'Measured',
    window_tokens: 200_000,
    reserve_tokens: 8_000,
    prompt_caching_enabled: true,
    rows_in_view: 10,
    ...overrides,
  };
}

function row(overrides: Partial<AgentContextRow> = {}): AgentContextRow {
  return {
    agentId: 'root',
    threadId: 'thread-1',
    parentAgentId: null,
    executionKind: 'Primary',
    observation: observation(),
    freshness: 'Stale',
    cacheTemperature: 'Hot',
    compaction: { state: 'None' },
    usage: {
      executionId: 'thread-1',
      inputTokens: 100,
      outputTokens: 40,
      cacheReadTokens: 0,
      cacheWriteTokens: 0,
      reasoningTokens: 0,
      totalTokens: 140,
      estimatedPublicCostMicros: 650,
      providerReportedCostMicros: null,
      preferredCostMicros: 650,
      costProvenance: 'PublicEstimate',
      estimatedCostCompleteness: 'Complete',
      attemptCount: 1,
    },
    ...overrides,
  };
}

function report(agents: AgentContextRow[] = [row()]): ConversationContextReport {
  return {
    rootThreadId: 'thread-1',
    schemaVersion: 1,
    generatedAtUtc: '2026-09-02T10:00:05Z',
    agents,
    total: {
      inputTokens: 100,
      outputTokens: 40,
      cacheReadTokens: 0,
      cacheWriteTokens: 0,
      reasoningTokens: 0,
      totalTokens: 140,
      preferredCostMicros: 650,
      costProvenance: 'PublicEstimate',
      costCompleteness: 'Complete',
      usageCompleteness: 'Complete',
    },
  };
}

function frame(overrides: Partial<ContextPressureMessage> = {}): ContextPressureMessage {
  return {
    $type: 'context_pressure',
    role: 'assistant',
    threadId: 'thread-1',
    agentId: 'root',
    generationOrdinal: 3,
    observedAtUtc: '2026-09-02T10:00:03Z',
    effectiveModelId: 'model-x',
    estimatedInputTokens: 6_000,
    measuredInputTokens: null,
    provenance: 'Estimated',
    windowTokens: 200_000,
    reserveTokens: 8_000,
    utilization: 6_000 / 192_000,
    rowsInView: 12,
    ...overrides,
  } as ContextPressureMessage;
}

describe('utilizationOf — the server formula, reproduced', () => {
  it('divides the size by the usable window (window minus reserve)', () => {
    expect(utilizationOf(5_000, 200_000, 8_000)).toBeCloseTo(5_000 / 192_000, 12);
  });

  it('is null when the window is unknown, zero, or eaten by the reserve', () => {
    expect(utilizationOf(5_000, null, 8_000)).toBeNull();
    expect(utilizationOf(5_000, 0, 0)).toBeNull();
    expect(utilizationOf(5_000, 8_000, 8_000)).toBeNull();
  });
});

describe('rowFromWire — one agent row', () => {
  it('reads a measured observation into a known capacity with the endpoint numbers', () => {
    const view = rowFromWire(row());
    expect(view.capacity).toEqual({
      kind: 'known',
      used: 5_000,
      window: 200_000,
      reserve: 8_000,
      utilization: 5_000 / 192_000,
      provenance: 'Measured',
    });
    expect(view.modelId).toBe('model-x');
    expect(view.generationOrdinal).toBe(2);
    expect(view.freshness).toBe('Stale');
    expect(view.cacheTemperature).toBe('Hot');
    expect(view.provisional).toBe(false);
  });

  it('falls back to the estimate when nothing was measured', () => {
    const view = rowFromWire(
      row({ observation: observation({ measured_input_tokens: null, provenance: 'Estimated' }) })
    );
    expect(view.capacity).toMatchObject({ kind: 'known', used: 4_000, provenance: 'Estimated' });
  });

  it('is UNKNOWN (no window) when the model window is not known — never 0%', () => {
    const view = rowFromWire(row({ observation: observation({ window_tokens: null }) }));
    expect(view.capacity).toEqual({ kind: 'unknown', reason: 'no-window' });
  });

  it('is UNKNOWN (no observation) when the loop was never observed', () => {
    const view = rowFromWire(row({ observation: null, freshness: 'None' }));
    expect(view.capacity).toEqual({ kind: 'unknown', reason: 'no-observation' });
  });

  it('is UNSUPPORTED for an excluded (provider-owned session) loop', () => {
    const view = rowFromWire(
      row({ observation: null, freshness: 'None', compaction: { state: 'Unsupported' } })
    );
    expect(view.capacity).toEqual({ kind: 'unknown', reason: 'unsupported' });
    expect(view.compaction.state).toBe('Unsupported');
  });

  it('separates "no usage recorded" from "usage with no price" from "$0"', () => {
    expect(rowFromWire(row({ usage: null })).cost).toEqual({ kind: 'none' });
    expect(rowFromWire(row({ usage: null })).tokens).toEqual({ kind: 'none' });

    const unpriced = rowFromWire(
      row({ usage: { ...row().usage!, preferredCostMicros: null, costProvenance: 'Unavailable' } })
    );
    expect(unpriced.cost).toEqual({ kind: 'unavailable' });
    expect(unpriced.tokens).toMatchObject({ kind: 'value', total: 140 });

    const free = rowFromWire(row({ usage: { ...row().usage!, preferredCostMicros: 0 } }));
    expect(free.cost).toMatchObject({ kind: 'value', micros: 0 });
  });

  it('carries the policy decision and the compaction reason when present', () => {
    const view = rowFromWire(
      row({
        observation: observation({ decision: { decision: 'Skipped', reason: 'cooldown_active' } }),
        compaction: { state: 'Rejected', checkpointId: 'cp-1', reason: 'validation_failed' },
      })
    );
    expect(view.compaction).toEqual({
      state: 'Rejected',
      checkpointId: 'cp-1',
      reason: 'validation_failed',
      decision: { decision: 'Skipped', reason: 'cooldown_active' },
    });
  });
});

describe('viewFromReport — rows plus the descendant-wide total', () => {
  it('keeps root first and reports the total from the same fold', () => {
    const child = row({
      agentId: 'agent-1',
      threadId: 'subagent-agent-1',
      parentAgentId: 'root',
      executionKind: 'SubAgent',
    });
    const view = viewFromReport(report([row(), child]));
    expect(view.rows.map((r) => r.agentId)).toEqual(['root', 'agent-1']);
    expect(view.total.tokens).toMatchObject({ kind: 'value', total: 140 });
    expect(view.total.cost).toEqual({
      kind: 'value',
      micros: 650,
      provenance: 'PublicEstimate',
      completeness: 'Complete',
    });
    expect(view.total.usageCompleteness).toBe('Complete');
    expect(view.generatedAtUtc).toBe('2026-09-02T10:00:05Z');
  });

  it('reports a total that was never persisted as "no usage" — not as 0 tokens, not as complete', () => {
    const r = report();
    r.total = {
      ...r.total,
      totalTokens: 0,
      preferredCostMicros: null,
      costProvenance: 'Unavailable',
      usageCompleteness: null,
    };
    const view = viewFromReport(r);
    expect(view.total.tokens).toEqual({ kind: 'none' });
    expect(view.total.cost).toEqual({ kind: 'none' });
    expect(view.total.usageCompleteness).toBeNull();
  });

  it('reports a persisted total with no priceable attempt as unavailable, keeping its tokens', () => {
    const r = report();
    r.total = { ...r.total, preferredCostMicros: null, costProvenance: 'Unavailable', usageCompleteness: 'Complete' };
    const view = viewFromReport(r);
    expect(view.total.tokens).toMatchObject({ kind: 'value', total: 140 });
    expect(view.total.cost).toEqual({ kind: 'unavailable' });
  });
});

describe('applyPressureFrame — live enrichment, never a downgrade', () => {
  it('updates the matching row from a newer frame and marks it Fresh', () => {
    const rows = viewFromReport(report()).rows;
    const next = applyPressureFrame(rows, frame());
    expect(next).not.toBe(rows);
    expect(next[0].capacity).toEqual({
      kind: 'known',
      used: 6_000,
      window: 200_000,
      reserve: 8_000,
      utilization: 6_000 / 192_000,
      provenance: 'Estimated',
    });
    expect(next[0].freshness).toBe('Fresh');
    expect(next[0].generationOrdinal).toBe(3);
    // Usage and cost are the endpoint's; a pressure frame carries neither.
    expect(next[0].tokens).toEqual(rows[0].tokens);
    expect(next[0].cost).toEqual(rows[0].cost);
  });

  it('ignores a frame older than the row it would replace', () => {
    const rows = viewFromReport(report()).rows;
    const next = applyPressureFrame(rows, frame({ generationOrdinal: 1 }));
    expect(next).toBe(rows);
  });

  it('inserts a provisional row for a thread the report has not seen yet', () => {
    const rows = viewFromReport(report()).rows;
    const next = applyPressureFrame(
      rows,
      frame({ threadId: 'subagent-agent-2', agentId: 'agent-2', generationOrdinal: 1 })
    );
    expect(next).toHaveLength(2);
    expect(next[1]).toMatchObject({
      agentId: 'agent-2',
      threadId: 'subagent-agent-2',
      executionKind: 'SubAgent',
      freshness: 'Fresh',
      provisional: true,
      tokens: { kind: 'none' },
      cost: { kind: 'none' },
      compaction: { state: 'None', decision: null },
    });
  });

  it('drops a frame with no thread id: no id, no row to paint', () => {
    const rows = viewFromReport(report()).rows;
    expect(applyPressureFrame(rows, frame({ threadId: null }))).toBe(rows);
  });

  it('reads a frame without a window as UNKNOWN rather than 0%', () => {
    const rows = viewFromReport(report()).rows;
    const next = applyPressureFrame(rows, frame({ windowTokens: null, utilization: null }));
    expect(next[0].capacity).toEqual({ kind: 'unknown', reason: 'no-window' });
  });
});

describe('labels — zero is never spelled like unknown, partial, stale, unavailable, unsupported, skipped, failed or rolled back', () => {
  it('capacity labels are distinct per state and name the provenance', () => {
    const known = capacityLabel({
      kind: 'known',
      used: 0,
      window: 200_000,
      reserve: 8_000,
      utilization: 0,
      provenance: 'Measured',
    });
    expect(known).toBe('0% of 200,000 tokens (measured)');
    expect(capacityLabel({ kind: 'unknown', reason: 'no-window' })).toBe('Unknown window');
    expect(capacityLabel({ kind: 'unknown', reason: 'no-observation' })).toBe('No observation');
    expect(capacityLabel({ kind: 'unknown', reason: 'unsupported' })).toBe(
      'Unsupported (provider-owned session)'
    );
  });

  it('cost labels: $0.0000 vs unavailable vs none, with partial flagged as a lower bound', () => {
    expect(formatMicros(0)).toBe('$0.0000');
    expect(formatMicros(12_345)).toBe('$0.0123');
    expect(costLabel({ kind: 'value', micros: 0, provenance: 'PublicEstimate', completeness: 'Complete' })).toBe(
      '$0.0000 (public estimate, complete)'
    );
    expect(costLabel({ kind: 'value', micros: 700, provenance: 'ProviderReported', completeness: 'Complete' })).toBe(
      '$0.0007 (provider-reported)'
    );
    expect(costLabel({ kind: 'value', micros: 700, provenance: 'PublicEstimate', completeness: 'Partial' })).toBe(
      '$0.0007 (public estimate, partial — lower bound)'
    );
    expect(costLabel({ kind: 'unavailable' })).toBe('Unavailable');
    expect(costLabel({ kind: 'none' })).toBe('No usage recorded');
  });

  it('freshness, temperature, compaction and decision labels are each distinct', () => {
    expect(freshnessLabel('Fresh')).toBe('Fresh');
    expect(freshnessLabel('Stale')).toBe('Stale');
    expect(freshnessLabel('None')).toBe('No observation');

    expect(temperatureLabel('Hot')).toBe('Hot cache');
    expect(temperatureLabel('Cold')).toBe('Cold cache');
    expect(temperatureLabel('Unknown')).toBe('Cache unknown');

    const labels = (['None', 'InFlight', 'Active', 'Rejected', 'RolledBack', 'Superseded', 'Unsupported'] as const).map(
      (state) => compactionLabel({ state, checkpointId: null, reason: null, decision: null })
    );
    expect(new Set(labels).size).toBe(labels.length);
    expect(compactionLabel({ state: 'RolledBack', checkpointId: 'cp-1', reason: null, decision: null })).toBe(
      'Compaction rolled back'
    );
    expect(compactionLabel({ state: 'Rejected', checkpointId: 'cp-1', reason: 'validation_failed', decision: null })).toBe(
      'Compaction rejected: validation_failed'
    );

    expect(decisionLabel({ decision: 'Skipped', reason: 'cooldown_active' })).toBe('Skipped: cooldown_active');
    expect(decisionLabel({ decision: 'Failed', reason: 'summary_model_error' })).toBe('Failed: summary_model_error');
    expect(decisionLabel({ decision: 'Compact', reason: null })).toBe('Compaction recommended');
    expect(decisionLabel({ decision: 'Warn', reason: null })).toBe('Warning: nearing the window');
    expect(decisionLabel({ decision: 'NoAction', reason: null })).toBe('No action');
    expect(decisionLabel(null)).toBe('No decision yet');
  });
});
