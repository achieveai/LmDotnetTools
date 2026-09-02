/**
 * Wire types for `GET /api/conversations/{id}/context` (#681, consumed by #685; spec 679 §4.3).
 *
 * The outer report is camelCase (ASP.NET web defaults). The embedded `observation` is the persisted
 * `ContextObservation` record, whose fields are pinned to snake_case by explicit `JsonPropertyName`
 * attributes on the C# type — so the two casings inside one payload are the contract, not a typo.
 *
 * Content-free by construction: counts, ratios, ids and statuses only. Nothing here can carry a
 * rendered prompt or a message body, and the panel that consumes it renders nothing but these.
 */

/** How a context-size number was obtained (`MeasurementProvenance`). */
export type MeasurementProvenance = 'Measured' | 'Estimated' | 'Unavailable';

/** Expected prompt-cache reuse (`CacheTemperature`, §4.4). */
export type CacheTemperature = 'Hot' | 'Cold' | 'Unknown';

/** Provenance of a resolved cost figure (`CostProvenance`). */
export type CostProvenance = 'Unavailable' | 'PublicEstimate' | 'ProviderReported';

/** Completeness of a public cost estimate (`CostCompleteness`, §4.5). */
export type CostCompleteness = 'Unavailable' | 'Partial' | 'Complete';

/** Whether the persisted usage ledger is still accumulating (`UsageCompleteness`). */
export type UsageCompleteness = 'InProgress' | 'Partial' | 'Complete';

/** The execution context that produced a row (`UsageExecutionKind`). */
export type UsageExecutionKind =
  | 'Primary'
  | 'SubAgent'
  | 'WorkflowController'
  | 'WorkflowTask'
  | 'Compaction';

/** How current an observation is (`ContextFreshness`, §4.5). */
export type ContextFreshness = 'Fresh' | 'Stale' | 'None';

/** The compaction state a row reports (`CompactionStates`, §3.5, §9). */
export type CompactionState =
  | 'None'
  | 'InFlight'
  | 'Active'
  | 'Rejected'
  | 'RolledBack'
  | 'Superseded'
  | 'Unsupported';

/** One policy decision stamped on an observation (`CompactionDecisionSummary`, §5.5). */
export interface CompactionDecisionSummary {
  /** NoAction | Warn | Shadow | Compact | Skipped | Failed. */
  decision: string;
  /** Typed reason for a Skipped or Failed decision (§5.6). */
  reason?: string | null;
  utilization?: number | null;
  tokens?: number;
  window?: number | null;
  reserve?: number;
  cache_temperature?: CacheTemperature;
  cooldown_remaining?: number | null;
  predicted_savings_micros?: number | null;
  cut_seq?: number | null;
}

/** `ContextObservation` as persisted and as embedded in a report row (snake_case on the wire). */
export interface ContextObservation {
  schema_version?: number;
  thread_id: string;
  agent_id: string;
  run_id?: string;
  generation_id?: string;
  generation_ordinal: number;
  observed_at_utc: string;
  effective_model_id: string;
  estimated_input_tokens: number;
  measured_input_tokens?: number | null;
  provenance: MeasurementProvenance;
  window_tokens?: number | null;
  reserve_tokens: number;
  prompt_caching_enabled?: boolean | null;
  active_checkpoint_id?: string | null;
  rows_in_view?: number;
  decision?: CompactionDecisionSummary | null;
}

/** One agent's compaction state (`AgentCompactionStatus`). */
export interface AgentCompactionStatus {
  state: CompactionState;
  checkpointId?: string | null;
  reason?: string | null;
}

/** One execution's spend, folded from the root ledger (`ExecutionUsageRow`). */
export interface ExecutionUsageRow {
  executionId: string;
  executionKinds?: UsageExecutionKind[];
  inputTokens: number;
  outputTokens: number;
  cacheReadTokens: number;
  cacheWriteTokens: number;
  reasoningTokens: number;
  totalTokens: number;
  estimatedPublicCostMicros?: number | null;
  providerReportedCostMicros?: number | null;
  preferredCostMicros?: number | null;
  costProvenance: CostProvenance;
  estimatedCostCompleteness: CostCompleteness;
  attemptCount?: number;
  compactionAttemptCount?: number;
}

/** One agent loop in the report (`AgentContextRow`). */
export interface AgentContextRow {
  /** `root` or the sub-agent id. */
  agentId: string;
  threadId: string;
  parentAgentId?: string | null;
  executionKind: UsageExecutionKind;
  observation?: ContextObservation | null;
  freshness: ContextFreshness;
  cacheTemperature: CacheTemperature;
  compaction: AgentCompactionStatus;
  usage?: ExecutionUsageRow | null;
}

/** The conversation total (`ConversationCostTotal`). */
export interface ConversationCostTotal {
  inputTokens: number;
  outputTokens: number;
  cacheReadTokens: number;
  cacheWriteTokens: number;
  reasoningTokens: number;
  totalTokens: number;
  preferredCostMicros?: number | null;
  costProvenance: CostProvenance;
  costCompleteness: CostCompleteness;
  /** Null when nothing was persisted yet. */
  usageCompleteness?: UsageCompleteness | null;
}

/** The whole payload (`ConversationContextReport`). */
export interface ConversationContextReport {
  rootThreadId: string;
  schemaVersion: number;
  generatedAtUtc: string;
  agents: AgentContextRow[];
  total: ConversationCostTotal;
}
