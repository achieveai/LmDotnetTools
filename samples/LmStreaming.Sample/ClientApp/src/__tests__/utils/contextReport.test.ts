import { describe, it, expect } from 'vitest';
import type {
  AgentContextRow,
  CompactionDecisionSummary,
  ConversationContextReport,
  ContextObservation,
} from '@/types/context';
import type { ContextPressureMessage } from '@/types/messages';
import {
  applyPressureFrame,
  capacityLabel,
  capacityScopeNote,
  compactionLabel,
  costLabel,
  decisionLabel,
  formatMicros,
  freshnessLabel,
  rowFromWire,
  temperatureLabel,
  tokenBreakdown,
  utilizationOf,
  viewFromReport,
} from '@/utils/contextReport';

describe('tokenBreakdown', () => {
  it('names every category and says which ones are already counted inside input or output', () => {
    // The shape of the screenshot that prompted this: cache read is inside input, thinking inside output.
    const lines = tokenBreakdown({
      kind: 'value',
      input: 23_235_406,
      output: 29_730,
      cacheRead: 22_102_481,
      cacheWrite: 0,
      reasoning: 4_547,
      total: 23_265_136,
    });

    expect(lines).toEqual([
      { key: 'input', label: 'Input', value: '23,235,406', note: null },
      { key: 'cache-read', label: 'Cache read', value: '22,102,481', note: 'part of input' },
      { key: 'uncached-input', label: 'Uncached input', value: '1,132,925', note: 'part of input' },
      { key: 'cache-write', label: 'Cache write', value: '0', note: null },
      { key: 'output', label: 'Output', value: '29,730', note: null },
      { key: 'thinking', label: 'Thinking', value: '4,547', note: 'part of output' },
      { key: 'total', label: 'Total', value: '23,265,136', note: null },
    ]);
  });

  it('treats cache reads above input as reported separately, the Anthropic shape', () => {
    // Anthropic's input_tokens excludes cache reads, so a cached row has cacheRead far above input.
    const lines = tokenBreakdown({
      kind: 'value',
      input: 12,
      output: 1,
      cacheRead: 48_000,
      cacheWrite: 0,
      reasoning: 0,
      total: 13,
    });

    expect(lines.find((l) => l.key === 'cache-read')?.note).toBe('reported separately');
    expect(lines.find((l) => l.key === 'uncached-input')).toMatchObject({ value: '12', note: null });
  });

  it('has no lines when no usage is recorded', () => {
    expect(tokenBreakdown({ kind: 'none' })).toEqual([]);
  });
});

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
      scope: 'history',
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
        observation: observation({ decision: { decision: 'skipped', reason: 'cooldown' } }),
        compaction: { state: 'Rejected', checkpointId: 'cp-1', reason: 'validation_failed' },
      })
    );
    expect(view.compaction).toEqual({
      state: 'Rejected',
      checkpointId: 'cp-1',
      reason: 'validation_failed',
      decision: { decision: 'skipped', reason: 'cooldown' },
      summaryFallback: null,
    });
  });
});

describe('capacity — the full request the compaction policy measures', () => {
  // The 18k test profile: ~10k of the policy's 12,880 tokens are the tool schemas and system prompt,
  // which the observation's own estimate (190) leaves out.
  const policy = { decision: 'warn', tokens: 12_880, utilization: 12_880 / 16_976, window: 18_000, reserve: 1_024 };
  const small = observation({
    estimated_input_tokens: 190,
    measured_input_tokens: null,
    provenance: 'Estimated',
    window_tokens: 18_000,
    reserve_tokens: 1_024,
  });

  it("uses the decision's tokens and utilization, marked as the full request", () => {
    const view = rowFromWire(row({ observation: { ...small, decision: policy } }));
    expect(view.capacity).toEqual({
      kind: 'known',
      used: 12_880,
      window: 18_000,
      reserve: 1_024,
      utilization: 12_880 / 16_976,
      provenance: 'Estimated',
      scope: 'request',
    });
  });

  it('prefers lastDecision, which the compaction state rests on', () => {
    const view = rowFromWire(
      row({
        observation: small,
        compaction: {
          state: 'Active',
          lastDecision: {
            decision: { ...policy, decision: 'compact', reason: 'hard', tokens: 15_576, utilization: 15_576 / 16_976 },
            generationOrdinal: 5,
            generationId: 'gen-5',
            decidedAtUtc: '2026-09-15T04:00:00Z',
            ageSeconds: 3,
          },
        },
      })
    );
    expect(view.capacity).toMatchObject({ kind: 'known', used: 15_576, scope: 'request' });
  });

  it("computes the utilization from the decision's window when the decision omits it", () => {
    const view = rowFromWire(
      row({ observation: { ...small, decision: { decision: 'warn', tokens: 12_880, window: 18_000, reserve: 1_024 } } })
    );
    expect(view.capacity).toMatchObject({ kind: 'known', used: 12_880, utilization: 12_880 / 16_976, scope: 'request' });
  });

  it('falls back to the observation estimate when there is no decision, or it carries no tokens', () => {
    expect(rowFromWire(row({ observation: small })).capacity).toMatchObject({ used: 190, scope: 'history' });
    expect(
      rowFromWire(row({ observation: { ...small, decision: { decision: 'no_action' } } })).capacity
    ).toMatchObject({ used: 190, scope: 'history' });
  });

  it('keeps a full-request figure when a live frame (history only) arrives, but takes its freshness', () => {
    const rows = viewFromReport(report([row({ observation: { ...small, decision: policy } })])).rows;
    const next = applyPressureFrame(rows, frame({ estimatedInputTokens: 300, windowTokens: 18_000, reserveTokens: 1_024, utilization: 300 / 16_976 }));
    expect(next[0].capacity).toEqual(rows[0].capacity);
    expect(next[0].generationOrdinal).toBe(3);
    expect(next[0].freshness).toBe('Fresh');
  });

  describe('after a compaction — the active checkpoint outranks the decision that caused it', () => {
    const checkpoint = { checkpointId: 'cp-1', estimatedTokensBefore: 15_576, estimatedTokensAfter: 10_300, generationOrdinal: 5 };
    const hard = { ...policy, decision: 'compact', reason: 'hard', tokens: 15_576, utilization: 15_576 / 16_976, cut_seq: 12 };
    const decided = (ordinal: number, decision: CompactionDecisionSummary = hard) => ({
      decision,
      generationOrdinal: ordinal,
      generationId: `gen-${ordinal}`,
      decidedAtUtc: '2026-09-15T04:00:00Z',
      ageSeconds: 1,
    });

    it("shows the checkpoint's after figure while the newest decision is the cut's own", () => {
      const view = rowFromWire(
        row({
          observation: { ...small, decision: hard, generation_ordinal: 5 },
          compaction: { state: 'Active', checkpointId: 'cp-1', lastDecision: decided(5), activeCheckpoint: checkpoint },
        })
      );
      expect(view.capacity).toEqual({
        kind: 'known',
        used: 10_300,
        window: 18_000,
        reserve: 1_024,
        utilization: 10_300 / 16_976,
        provenance: 'Estimated',
        scope: 'request',
        afterCompaction: true,
      });
    });

    it('shows the after figure when no decision was recorded, and for an older decision', () => {
      expect(
        rowFromWire(row({ observation: small, compaction: { state: 'Active', activeCheckpoint: checkpoint } })).capacity
      ).toMatchObject({ used: 10_300, window: 18_000, reserve: 1_024, afterCompaction: true });
      expect(
        rowFromWire(row({ observation: small, compaction: { state: 'Active', lastDecision: decided(4), activeCheckpoint: checkpoint } }))
          .capacity
      ).toMatchObject({ used: 10_300, afterCompaction: true });
    });

    it('goes back to the decision once a later turn has been decided', () => {
      const view = rowFromWire(
        row({
          observation: small,
          compaction: { state: 'Active', lastDecision: decided(6, policy), activeCheckpoint: checkpoint },
        })
      );
      expect(view.capacity).toMatchObject({ used: 12_880, scope: 'request' });
      expect(view.capacity).not.toHaveProperty('afterCompaction');
    });

    it("orders by the observation's generation when the report has no lastDecision", () => {
      const at = (ordinal: number) =>
        rowFromWire(
          row({
            observation: { ...small, decision: policy, generation_ordinal: ordinal },
            compaction: { state: 'Active', activeCheckpoint: checkpoint },
          })
        ).capacity;
      expect(at(5)).toMatchObject({ used: 10_300, afterCompaction: true });
      expect(at(6)).toMatchObject({ used: 12_880 });
    });

    it("keeps today's figure when the field is absent or null", () => {
      const base = { observation: small, compaction: { state: 'Active' as const, lastDecision: decided(5) } };
      expect(rowFromWire(row(base)).capacity).toMatchObject({ used: 15_576 });
      expect(
        rowFromWire(row({ ...base, compaction: { ...base.compaction, activeCheckpoint: null } })).capacity
      ).toMatchObject({ used: 15_576 });
    });

    it('carries the failure a summary fallback replaced, and null for a checkpoint with a model summary', () => {
      const fallback = rowFromWire(
        row({
          observation: small,
          compaction: { state: 'Active', activeCheckpoint: { ...checkpoint, summaryFallback: 'validation_failed:V3' } },
        })
      );
      expect(fallback.compaction.summaryFallback).toBe('validation_failed:V3');
      // The after figure is still the checkpoint's: a fallback cut is a real cut.
      expect(fallback.capacity).toMatchObject({ used: 10_300, afterCompaction: true });

      const summarized = (activeCheckpoint: typeof checkpoint | null | undefined, extra: object = {}) =>
        rowFromWire(row({ observation: small, compaction: { state: 'Active', activeCheckpoint: activeCheckpoint && { ...activeCheckpoint, ...extra } } }))
          .compaction.summaryFallback;
      expect(summarized(checkpoint)).toBeNull();
      expect(summarized(checkpoint, { summaryFallback: null })).toBeNull();
      expect(summarized(null)).toBeNull();
      expect(summarized(undefined)).toBeNull();
    });

    it('says the figure is after compaction in the label and the note', () => {
      const capacity = rowFromWire(
        row({ observation: small, compaction: { state: 'Active', activeCheckpoint: checkpoint } })
      ).capacity;
      expect(capacityLabel(capacity)).toBe('61% of 18,000 tokens (estimated, after compaction)');
      expect(capacityScopeNote(capacity)).toBe('After compaction. Includes tool definitions and the system prompt');
    });
  });

  it('notes that the full-request figure includes tool definitions and the system prompt', () => {
    const request = rowFromWire(row({ observation: { ...small, decision: policy } })).capacity;
    expect(capacityScopeNote(request)).toBe('Includes tool definitions and the system prompt');
    expect(capacityScopeNote(rowFromWire(row({ observation: small })).capacity)).toBeNull();
    expect(capacityScopeNote({ kind: 'unknown', reason: 'no-window' })).toBeNull();
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
      scope: 'history',
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
      scope: 'history',
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

    // The wire values are CompactionDecisionKinds (CompactionPolicy.cs): snake_case strings, not enum names.
    expect(decisionLabel({ decision: 'skipped', reason: 'cooldown' })).toBe('Skipped: cooldown');
    expect(decisionLabel({ decision: 'failed', reason: 'summary_call_failed' })).toBe('Failed: summary_call_failed');
    expect(decisionLabel({ decision: 'compact', reason: null })).toBe('Compaction recommended');
    expect(decisionLabel({ decision: 'compact', reason: 'summary_fallback' })).toBe('Compacted (no summary)');
    expect(decisionLabel({ decision: 'compact', reason: 'summary_fallback', ageSeconds: 45 })).toBe('Compacted (no summary) · 45s ago');
    expect(decisionLabel({ decision: 'shadow', reason: null })).toBe('Shadow compaction');
    expect(decisionLabel({ decision: 'warn', reason: null })).toBe('Warning: nearing the window');
    expect(decisionLabel({ decision: 'no_action', reason: null })).toBe('No action');
    expect(decisionLabel(null)).toBe('No decision yet');
  });
});

// The report's `compaction.lastDecision` is the newest generation the policy actually decided on. The latest
// observation often carries no decision (a wrap-up turn, or a live measurement ahead of the policy's stamp),
// so reading only the observation showed "No decision yet" for a loop that had decided a minute earlier.
describe('lastDecision — the decision the compaction state rests on, with its age', () => {
  function lastDecision(decision: string, reason: string | null, ageSeconds: number) {
    return {
      decision: { decision, reason },
      generationOrdinal: 1,
      generationId: 'gen-1',
      decidedAtUtc: '2026-09-02T09:58:00Z',
      ageSeconds,
    };
  }

  it('uses lastDecision when the shown observation has no decision', () => {
    const view = rowFromWire(
      row({
        observation: observation({ decision: null }),
        compaction: { state: 'Active', checkpointId: 'cp-1', lastDecision: lastDecision('compact', 'hard', 125) },
      })
    );
    expect(view.compaction.decision).toEqual({ decision: 'compact', reason: 'hard', ageSeconds: 125 });
  });

  it('prefers lastDecision over the observation decision', () => {
    const view = rowFromWire(
      row({
        observation: observation({ decision: { decision: 'no_action', reason: null } }),
        compaction: { state: 'None', lastDecision: lastDecision('skipped', 'cooldown', 30) },
      })
    );
    expect(view.compaction.decision).toEqual({ decision: 'skipped', reason: 'cooldown', ageSeconds: 30 });
  });

  it('has no decision when neither lastDecision nor the observation carries one', () => {
    const view = rowFromWire(row({ observation: observation({ decision: null }), compaction: { state: 'None', lastDecision: null } }));
    expect(view.compaction.decision).toBeNull();
    expect(decisionLabel(view.compaction.decision)).toBe('No decision yet');
  });

  it('labels the decision with a compact age', () => {
    expect(decisionLabel({ decision: 'compact', reason: null, ageSeconds: 125 })).toBe('Compaction recommended · 2m ago');
    expect(decisionLabel({ decision: 'skipped', reason: 'cooldown', ageSeconds: 45 })).toBe('Skipped: cooldown · 45s ago');
    expect(decisionLabel({ decision: 'no_action', reason: null, ageSeconds: 0 })).toBe('No action · just now');
    expect(decisionLabel({ decision: 'warn', reason: null, ageSeconds: 7_260 })).toBe('Warning: nearing the window · 2h ago');
    expect(decisionLabel({ decision: 'failed', reason: 'summary_call_failed', ageSeconds: 200_000 })).toBe(
      'Failed: summary_call_failed · 2d ago'
    );
  });

  it('omits the age when it is unknown (a decision read from the observation alone)', () => {
    expect(decisionLabel({ decision: 'compact', reason: null, ageSeconds: null })).toBe('Compaction recommended');
    expect(decisionLabel({ decision: 'compact', reason: null })).toBe('Compaction recommended');
  });
});
