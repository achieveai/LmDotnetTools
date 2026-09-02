import type {
  AgentContextRow,
  CacheTemperature,
  CompactionState,
  ContextFreshness,
  ConversationContextReport,
  CostCompleteness,
  CostProvenance,
  MeasurementProvenance,
  UsageCompleteness,
  UsageExecutionKind,
} from '@/types/context';
import type { ContextPressureMessage } from '@/types/messages';

/**
 * View model behind the context/cost panel (#685; spec 679 §4.3, §7.1).
 *
 * Every wire row is normalized into a small discriminated union per column so the component can
 * render each state with its OWN label and its own `aria` text — zero is a value, never a stand-in
 * for unknown / partial / stale / unavailable / unsupported / skipped / failed. The helpers are pure
 * so the composable (which owns fetch + live merge) and the component (which only renders) derive
 * identical rows from identical inputs, and so the state labels can be pinned in one place.
 */

/** How full the model's window is, when the panel can say. */
export type CapacityView =
  | {
      kind: 'known';
      /** Measured when the provider reported it, else the pre-send estimate. */
      used: number;
      window: number;
      reserve: number;
      /** `used / (window - reserve)`, the server's formula. */
      utilization: number;
      provenance: MeasurementProvenance;
    }
  | {
      kind: 'unknown';
      /** Why there is no gauge: never observed, model window unknown, or an excluded loop (§9). */
      reason: 'no-observation' | 'no-window' | 'unsupported';
    };

/** Token counts for one execution or the whole tree. */
export type TokensView =
  | {
      kind: 'value';
      input: number;
      output: number;
      cacheRead: number;
      cacheWrite: number;
      reasoning: number;
      total: number;
    }
  | { kind: 'none' };

/** The preferred dollar figure with its provenance and completeness. */
export type CostView =
  | { kind: 'value'; micros: number; provenance: CostProvenance; completeness: CostCompleteness }
  /** Usage exists but no contributing attempt could be priced. */
  | { kind: 'unavailable' }
  /** No usage row at all. */
  | { kind: 'none' };

export interface DecisionView {
  decision: string;
  reason: string | null;
}

export interface CompactionView {
  state: CompactionState;
  checkpointId: string | null;
  reason: string | null;
  /** The policy's latest decision for this loop, when the policy ran (§5.5). */
  decision: DecisionView | null;
}

export interface ContextRowView {
  agentId: string;
  threadId: string;
  parentAgentId: string | null;
  executionKind: UsageExecutionKind;
  modelId: string | null;
  capacity: CapacityView;
  tokens: TokensView;
  cost: CostView;
  freshness: ContextFreshness;
  cacheTemperature: CacheTemperature;
  compaction: CompactionView;
  generationOrdinal: number | null;
  observedAtUtc: string | null;
  /** True for a row built from a live frame the authoritative report has not listed yet. */
  provisional: boolean;
}

export interface ContextTotalView {
  tokens: TokensView;
  cost: CostView;
  usageCompleteness: UsageCompleteness | null;
}

export interface ContextView {
  rows: ContextRowView[];
  total: ContextTotalView;
  generatedAtUtc: string | null;
}

/** The server's `ContextObservation.Utilization`, reproduced so live and reload agree by construction. */
export function utilizationOf(
  used: number,
  window: number | null | undefined,
  reserve: number
): number | null {
  if (window == null || window <= 0) return null;
  const usable = window - reserve;
  return usable > 0 ? used / usable : null;
}

function capacityFromNumbers(
  used: number,
  window: number | null | undefined,
  reserve: number,
  provenance: MeasurementProvenance
): CapacityView {
  const utilization = utilizationOf(used, window, reserve);
  if (utilization === null || window == null) {
    return { kind: 'unknown', reason: 'no-window' };
  }
  return { kind: 'known', used, window, reserve, utilization, provenance };
}

function asProvenance(value: string | null | undefined): MeasurementProvenance {
  return value === 'Measured' || value === 'Estimated' ? value : 'Unavailable';
}

/** Normalizes one wire row. */
export function rowFromWire(row: AgentContextRow): ContextRowView {
  const observation = row.observation ?? null;
  const usage = row.usage ?? null;

  let capacity: CapacityView;
  if (row.compaction?.state === 'Unsupported') {
    capacity = { kind: 'unknown', reason: 'unsupported' };
  } else if (!observation) {
    capacity = { kind: 'unknown', reason: 'no-observation' };
  } else {
    capacity = capacityFromNumbers(
      observation.measured_input_tokens ?? observation.estimated_input_tokens,
      observation.window_tokens,
      observation.reserve_tokens,
      asProvenance(observation.provenance)
    );
  }

  const tokens: TokensView = usage
    ? {
        kind: 'value',
        input: usage.inputTokens,
        output: usage.outputTokens,
        cacheRead: usage.cacheReadTokens,
        cacheWrite: usage.cacheWriteTokens,
        reasoning: usage.reasoningTokens,
        total: usage.totalTokens,
      }
    : { kind: 'none' };

  const cost: CostView = !usage
    ? { kind: 'none' }
    : usage.preferredCostMicros == null
      ? { kind: 'unavailable' }
      : {
          kind: 'value',
          micros: usage.preferredCostMicros,
          provenance: usage.costProvenance,
          completeness: usage.estimatedCostCompleteness,
        };

  const decision = observation?.decision
    ? { decision: observation.decision.decision, reason: observation.decision.reason ?? null }
    : null;

  return {
    agentId: row.agentId,
    threadId: row.threadId,
    parentAgentId: row.parentAgentId ?? null,
    executionKind: row.executionKind,
    modelId: observation?.effective_model_id ?? null,
    capacity,
    tokens,
    cost,
    freshness: row.freshness,
    cacheTemperature: row.cacheTemperature ?? 'Unknown',
    compaction: {
      state: row.compaction?.state ?? 'None',
      checkpointId: row.compaction?.checkpointId ?? null,
      reason: row.compaction?.reason ?? null,
      decision,
    },
    generationOrdinal: observation?.generation_ordinal ?? null,
    observedAtUtc: observation?.observed_at_utc ?? null,
    provisional: false,
  };
}

/** Normalizes the whole report: rows in report order (root first) plus the total from the same fold. */
export function viewFromReport(report: ConversationContextReport): ContextView {
  const t = report.total;
  return {
    rows: report.agents.map(rowFromWire),
    total: {
      tokens: {
        kind: 'value',
        input: t.inputTokens,
        output: t.outputTokens,
        cacheRead: t.cacheReadTokens,
        cacheWrite: t.cacheWriteTokens,
        reasoning: t.reasoningTokens,
        total: t.totalTokens,
      },
      cost:
        t.preferredCostMicros == null
          ? { kind: 'unavailable' }
          : {
              kind: 'value',
              micros: t.preferredCostMicros,
              provenance: t.costProvenance,
              completeness: t.costCompleteness,
            },
      usageCompleteness: t.usageCompleteness ?? null,
    },
    generatedAtUtc: report.generatedAtUtc ?? null,
  };
}

/**
 * Applies one live `context_pressure` frame to the rows.
 *
 * Returns the SAME array when nothing changes, so callers can compare by identity. A frame updates
 * only the observation-derived columns of its row (capacity, model, ordinal, time) and marks it
 * `Fresh` — a live loop just vouched for it. Usage and cost stay the endpoint's: the frame carries
 * neither. A frame older than the row (by generation ordinal) is ignored, so a late-arriving frame
 * can never downgrade a fresher figure. A frame for a thread the report has not listed inserts a
 * provisional row — typically a sub-agent spawned since the last hydrate — which the next hydrate
 * replaces with the authoritative one.
 */
export function applyPressureFrame(
  rows: ContextRowView[],
  frame: ContextPressureMessage
): ContextRowView[] {
  if (!frame.threadId) return rows;

  const capacity = capacityFromNumbers(
    frame.measuredInputTokens ?? frame.estimatedInputTokens,
    frame.windowTokens,
    frame.reserveTokens,
    asProvenance(frame.provenance)
  );
  // The frame's own utilization is the server's figure; prefer it over the recomputation when both
  // exist so a rounding difference cannot make the live and reload views disagree.
  if (capacity.kind === 'known' && typeof frame.utilization === 'number') {
    capacity.utilization = frame.utilization;
  }

  const index = rows.findIndex((r) => r.threadId === frame.threadId);
  if (index === -1) {
    return [
      ...rows,
      {
        agentId: frame.agentId,
        threadId: frame.threadId,
        parentAgentId: null,
        executionKind: frame.agentId === 'root' ? 'Primary' : 'SubAgent',
        modelId: frame.effectiveModelId ?? null,
        capacity,
        tokens: { kind: 'none' },
        cost: { kind: 'none' },
        freshness: 'Fresh',
        cacheTemperature: 'Unknown',
        compaction: { state: 'None', checkpointId: null, reason: null, decision: null },
        generationOrdinal: frame.generationOrdinal,
        observedAtUtc: frame.observedAtUtc ?? null,
        provisional: true,
      },
    ];
  }

  const current = rows[index];
  if (current.generationOrdinal !== null && frame.generationOrdinal < current.generationOrdinal) {
    return rows;
  }

  const next = rows.slice();
  next[index] = {
    ...current,
    modelId: frame.effectiveModelId ?? current.modelId,
    capacity,
    freshness: 'Fresh',
    generationOrdinal: frame.generationOrdinal,
    observedAtUtc: frame.observedAtUtc ?? current.observedAtUtc,
  };
  return next;
}

// ---------------------------------------------------------------------------------------------
// Labels. One string per state; the tests pin that no two states share one (§7.1).
// ---------------------------------------------------------------------------------------------

export function formatTokens(n: number): string {
  return n.toLocaleString('en-US');
}

/** Four decimals, matching the usage banner. `$0.0000` is a value; unavailability has its own words. */
export function formatMicros(micros: number): string {
  return `$${(micros / 1_000_000).toFixed(4)}`;
}

export function formatPercent(utilization: number): string {
  const pct = utilization * 100;
  // One decimal below 10% so a small-but-real figure does not round to the zero it is not.
  return pct > 0 && pct < 10 ? `${pct.toFixed(1)}%` : `${Math.round(pct)}%`;
}

const PROVENANCE_LABEL: Record<MeasurementProvenance, string> = {
  Measured: 'measured',
  Estimated: 'estimated',
  Unavailable: 'unavailable',
};

export function capacityLabel(capacity: CapacityView): string {
  if (capacity.kind === 'known') {
    return `${formatPercent(capacity.utilization)} of ${formatTokens(capacity.window)} tokens (${PROVENANCE_LABEL[capacity.provenance]})`;
  }
  switch (capacity.reason) {
    case 'no-window':
      return 'Unknown window';
    case 'no-observation':
      return 'No observation';
    case 'unsupported':
      return 'Unsupported (provider-owned session)';
  }
}

const COST_PROVENANCE_LABEL: Record<CostProvenance, string> = {
  ProviderReported: 'provider-reported',
  PublicEstimate: 'public estimate',
  Unavailable: 'unavailable',
};

const COST_COMPLETENESS_LABEL: Record<CostCompleteness, string> = {
  Complete: 'complete',
  Partial: 'partial — lower bound',
  Unavailable: 'unavailable',
};

export function costLabel(cost: CostView): string {
  switch (cost.kind) {
    case 'none':
      return 'No usage recorded';
    case 'unavailable':
      return 'Unavailable';
    case 'value': {
      const qualifiers =
        cost.provenance === 'ProviderReported'
          ? [COST_PROVENANCE_LABEL.ProviderReported]
          : [COST_PROVENANCE_LABEL[cost.provenance], COST_COMPLETENESS_LABEL[cost.completeness]];
      return `${formatMicros(cost.micros)} (${qualifiers.join(', ')})`;
    }
  }
}

export function tokensLabel(tokens: TokensView): string {
  return tokens.kind === 'none' ? 'No usage recorded' : `${formatTokens(tokens.total)} tokens`;
}

export function freshnessLabel(freshness: ContextFreshness): string {
  switch (freshness) {
    case 'Fresh':
      return 'Fresh';
    case 'Stale':
      return 'Stale';
    case 'None':
      return 'No observation';
  }
}

export function temperatureLabel(temperature: CacheTemperature): string {
  switch (temperature) {
    case 'Hot':
      return 'Hot cache';
    case 'Cold':
      return 'Cold cache';
    case 'Unknown':
      return 'Cache unknown';
  }
}

const COMPACTION_STATE_LABEL: Record<CompactionState, string> = {
  None: 'No compaction',
  InFlight: 'Compaction in flight',
  Active: 'Compacted',
  Rejected: 'Compaction rejected',
  RolledBack: 'Compaction rolled back',
  Superseded: 'Checkpoint superseded',
  Unsupported: 'Unsupported (provider-owned session)',
};

export function compactionLabel(compaction: CompactionView): string {
  const base = COMPACTION_STATE_LABEL[compaction.state] ?? compaction.state;
  return compaction.reason ? `${base}: ${compaction.reason}` : base;
}

export function decisionLabel(decision: DecisionView | null): string {
  if (!decision) return 'No decision yet';
  switch (decision.decision) {
    case 'NoAction':
      return 'No action';
    case 'Warn':
      return 'Warning: nearing the window';
    case 'Shadow':
      return 'Shadow compaction';
    case 'Compact':
      return 'Compaction recommended';
    case 'Skipped':
      return `Skipped: ${decision.reason ?? 'unspecified'}`;
    case 'Failed':
      return `Failed: ${decision.reason ?? 'unspecified'}`;
    default:
      return decision.reason ? `${decision.decision}: ${decision.reason}` : decision.decision;
  }
}

const USAGE_COMPLETENESS_LABEL: Record<UsageCompleteness, string> = {
  InProgress: 'in progress',
  Partial: 'partial — lower bound',
  Complete: 'complete',
};

export function usageCompletenessLabel(completeness: UsageCompleteness | null): string {
  return completeness ? USAGE_COMPLETENESS_LABEL[completeness] : 'not persisted';
}

const EXECUTION_KIND_LABEL: Record<UsageExecutionKind, string> = {
  Primary: 'primary',
  SubAgent: 'sub-agent',
  WorkflowController: 'workflow controller',
  WorkflowTask: 'workflow task',
  Compaction: 'compaction',
};

export function executionKindLabel(kind: UsageExecutionKind): string {
  return EXECUTION_KIND_LABEL[kind] ?? String(kind);
}
