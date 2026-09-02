import type { TodoTask } from './todo';

/**
 * Message type discriminators matching C# IMessageJsonConverter.GetDiscriminatorFromType()
 */
export const MessageType = {
  Text: 'text',
  TextUpdate: 'text_update',
  Image: 'image',
  ToolsCall: 'tools_call',
  ToolCall: 'tool_call',
  ToolsCallUpdate: 'tools_call_update',
  ToolCallUpdate: 'tool_call_update',
  ToolCallResult: 'tool_call_result',
  ToolsCallResult: 'tools_call_result',
  ToolsCallAggregate: 'tools_call_aggregate',
  Usage: 'usage',
  Reasoning: 'reasoning',
  ReasoningUpdate: 'reasoning_update',
  // Lifecycle messages from MultiTurnAgentLoop
  RunAssignment: 'run_assignment',
  RunCompleted: 'run_completed',
  // Server-side tool messages (built-in tools like web_search)
  ServerToolUse: 'server_tool_use',
  ServerToolResult: 'server_tool_result',
  TextWithCitations: 'text_with_citations',
  // Out-of-band notification (async sub-agent completion, context discovery, monitors, timers)
  Notify: 'notify',
  // One agent speaking to another inside a collaboration (#244)
  Agent: 'agent',
  // Live-only conversation-wide usage frame (folded across sub-agents/workflow descendants, #196)
  ConversationUsage: 'conversation_usage',
  // Live-only ToDo-board snapshot for the work panel (#583)
  ConversationTodo: 'conversation_todo',
  // Live-only per-agent context pressure frame for the context/cost panel (#681 → #685)
  ContextPressure: 'context_pressure',
} as const;

export type MessageTypeValue = (typeof MessageType)[keyof typeof MessageType];

/**
 * Role enum matching C# Role enum
 */
export type Role = 'none' | 'user' | 'assistant' | 'system' | 'tool';

/**
 * ExecutionTarget enum matching C# ExecutionTarget enum (JsonStringEnumConverter output)
 */
export type ExecutionTarget = 'LocalFunction' | 'ProviderServer';

/**
 * Base message interface matching C# IMessage
 */
export interface IMessage {
  $type: MessageTypeValue;
  role: Role;
  fromAgent?: string | null;
  generationId?: string | null;
  threadId?: string | null;
  runId?: string | null;
  parentRunId?: string | null;
  messageOrderIdx?: number | null;
}

/**
 * TextMessage matching C# TextMessage.cs
 */
export interface TextMessage extends IMessage {
  $type: typeof MessageType.Text;
  text: string;
  isThinking?: boolean;
  /**
   * Marker the backend sets via TextMessage.Metadata["context_discovery"] when a sandbox
   * context file (CLAUDE.md / AGENTS.md) is injected mid-session. ShadowPropertiesJsonConverter
   * flattens it to a top-level field on the wire, so the chat client can render a "Context
   * loaded" pill without inspecting message text. Absent on every other message.
   */
  context_discovery?: ContextDiscoveryMetadata;
}

/**
 * Metadata for a sandbox-discovered context file that was injected into the conversation.
 * Mirrors the keys the C# ContextDiscoveryInjector packs into
 * <c>TextMessage.Metadata["context_discovery"]</c>.
 */
export interface ContextDiscoveryMetadata {
  path: string;
  truncated?: boolean;
}

/**
 * TextUpdateMessage matching C# TextMessageUpdate.cs
 */
export interface TextUpdateMessage extends IMessage {
  $type: typeof MessageType.TextUpdate;
  text: string;
  isUpdate: true;
  isThinking?: boolean;
  chunkIdx?: number | null;
}

/**
 * ToolCall matching C# ToolCall.cs
 */
export interface ToolCall {
  function_name?: string | null;
  function_args?: string | null;
  tool_call_id?: string | null;
  index?: number;
  toolCallIdx?: number;
  execution_target?: ExecutionTarget;
  result?: string | null; // Result from ToolCallResultMessage
}

/**
 * ToolCallUpdate matching C# ToolCallUpdate record
 */
export interface ToolCallUpdate {
  tool_call_id?: string | null;
  index?: number;
  function_name?: string | null;
  function_args?: string | null;
  execution_target?: ExecutionTarget;
}

/**
 * ToolsCallMessage matching C# ToolsCallMessage.cs
 */
export interface ToolsCallMessage extends IMessage {
  $type: typeof MessageType.ToolsCall;
  tool_calls: ToolCall[];
}

/**
 * ToolsCallUpdateMessage matching C# ToolsCallUpdateMessage record
 */
export interface ToolsCallUpdateMessage extends IMessage {
  $type: typeof MessageType.ToolsCallUpdate;
  tool_call_updates: ToolCallUpdate[];
  chunkIdx?: number | null;
}

/**
 * ToolCallResultMessage matching C# ToolCallResultMessage.cs
 */
export interface ToolCallResultMessage extends IMessage {
  $type: typeof MessageType.ToolCallResult;
  tool_call_id?: string | null;
  tool_name?: string | null;
  result: string;
  is_error?: boolean;
  error_code?: string | null;
  execution_target?: ExecutionTarget;
  /**
   * True while this call is durably parked awaiting a browser-side answer (#246, e.g.
   * `AskUserQuestion`) — see `ToolHandlerResult.Deferred` / `ToolCallResultBuilder`. The
   * placeholder result arrives with `result: ''` and `is_deferred: true`; when resolved the SAME
   * `tool_call_id` gets a follow-up `ToolCallResultMessage` with the real `result` and
   * `is_deferred` false/absent, which overwrites the placeholder in the client's tool-results map.
   */
  is_deferred?: boolean;
  /** Unix millis when the call was deferred. Present only alongside `is_deferred: true`. */
  deferred_at?: number | null;
  /** Unix millis when the deferred call was resolved. Present only on the resolved follow-up. */
  resolved_at?: number | null;
}

/**
 * ToolsCallResultMessage matching C# ToolsCallResultMessage.cs
 */
export interface ToolsCallResultMessage extends IMessage {
  $type: typeof MessageType.ToolsCallResult;
  tool_call_results: ToolCallResultMessage[];
}

/**
 * Usage matching C# Usage.cs
 */
export interface Usage {
  prompt_tokens?: number;
  completion_tokens?: number;
  total_tokens?: number;
  total_cost?: number | null;
  input_tokens_details?: { cached_tokens?: number } | null;
  cache_creation_input_tokens?: number;
  // Legacy camelCase aliases (some providers may use these)
  inputTokens?: number;
  outputTokens?: number;
  cacheCreationTokens?: number;
  cacheReadTokens?: number;
}

/**
 * UsageMessage matching C# UsageMessage.cs
 */
export interface UsageMessage extends IMessage {
  $type: typeof MessageType.Usage;
  usage: Usage;
}

/**
 * ReasoningVisibility matching C# ReasoningVisibility enum
 */
export type ReasoningVisibility = 'Plain' | 'Summary' | 'Encrypted';
export type ReasoningVisibilityValue = ReasoningVisibility | 0 | 1 | 2;

/**
 * Normalize visibility values from backend (string or numeric enum) to string labels.
 */
export function normalizeReasoningVisibility(
  visibility: ReasoningVisibilityValue | null | undefined
): ReasoningVisibility | undefined {
  if (visibility === null || visibility === undefined) return undefined;
  if (visibility === 'Plain' || visibility === 0) return 'Plain';
  if (visibility === 'Summary' || visibility === 1) return 'Summary';
  if (visibility === 'Encrypted' || visibility === 2) return 'Encrypted';
  return undefined;
}

/**
 * ReasoningMessage matching C# ReasoningMessage.cs
 */
export interface ReasoningMessage extends IMessage {
  $type: typeof MessageType.Reasoning;
  reasoning: string;
  visibility?: ReasoningVisibilityValue;
}

/**
 * ReasoningUpdateMessage matching C# ReasoningUpdateMessage.cs
 */
export interface ReasoningUpdateMessage extends IMessage {
  $type: typeof MessageType.ReasoningUpdate;
  reasoning: string;
  isUpdate: true;
  visibility?: ReasoningVisibilityValue | null;
  chunkIdx?: number | null;
}

/**
 * RunAssignment matching C# RunAssignment record
 */
export interface RunAssignment {
  runId: string;
  inputIds: string[];
  generationId: string;
  parentRunId?: string | null;
}

/**
 * RunAssignmentMessage matching C# RunAssignmentMessage.cs
 */
export interface RunAssignmentMessage extends IMessage {
  $type: typeof MessageType.RunAssignment;
  Assignment: RunAssignment;
}

/**
 * RunCompletedMessage matching C# RunCompletedMessage.cs
 */
export interface RunCompletedMessage extends IMessage {
  $type: typeof MessageType.RunCompleted;
  completedRunId: string;
  wasForked: boolean;
  forkedToRunId?: string | null;
  hasPendingMessages: boolean;
  pendingMessageCount: number;
  isError?: boolean;
  errorMessage?: string | null;
}

/**
 * ToolCallMessage matching C# ToolCallMessage.cs (individual tool call, not aggregate)
 */
export interface ToolCallMessage extends IMessage {
  $type: typeof MessageType.ToolCall;
  tool_call_id?: string | null;
  function_name?: string | null;
  function_args?: string | null;
  execution_target?: ExecutionTarget;
  result?: string | null; // Result from ToolCallResultMessage
}

/**
 * ToolCallUpdateMessage matching C# ToolCallUpdateMessage.cs
 */
export interface ToolCallUpdateMessage extends IMessage {
  $type: typeof MessageType.ToolCallUpdate;
  tool_call_id?: string | null;
  function_name?: string | null;
  function_args?: string | null;
  execution_target?: ExecutionTarget;
  chunkIdx?: number | null;
}

/**
 * ImageMessage matching C# ImageMessage.cs
 */
export interface ImageMessage extends IMessage {
  $type: typeof MessageType.Image;
  image_data: string;
  media_type: string;
}

/**
 * ServerToolUseMessage matching C# ServerToolUseMessage.cs
 * Represents server-side tool invocation (e.g., web_search, web_fetch, code_execution)
 */
export interface ServerToolUseMessage extends IMessage {
  $type: typeof MessageType.ServerToolUse;
  // Legacy server_tool_use shape
  tool_use_id?: string;
  tool_name?: string;
  input?: unknown;
  // Unified ToolCallMessage shape (execution_target=ProviderServer)
  tool_call_id?: string | null;
  function_name?: string | null;
  function_args?: string | null;
  execution_target?: ExecutionTarget;
}

/**
 * ServerToolResultMessage matching C# ServerToolResultMessage.cs
 * Represents results from server-side tool execution
 */
export interface ServerToolResultMessage extends IMessage {
  $type: typeof MessageType.ServerToolResult;
  // Legacy server_tool_result shape
  tool_use_id?: string;
  tool_name?: string;
  result?: unknown;
  is_error?: boolean;
  error_code?: string | null;
  // Unified ToolCallResultMessage shape (execution_target=ProviderServer)
  tool_call_id?: string | null;
  function_name?: string | null;
  isError?: boolean;
  errorCode?: string | null;
  execution_target?: ExecutionTarget;
}

/**
 * CitationInfo matching C# CitationInfo record
 */
export interface CitationInfo {
  type?: string;
  url?: string | null;
  title?: string | null;
  cited_text?: string | null;
  start_index?: number | null;
  end_index?: number | null;
}

/**
 * TextWithCitationsMessage matching C# TextWithCitationsMessage.cs
 * Text content with citation references from server-side tools
 */
export interface TextWithCitationsMessage extends IMessage {
  $type: typeof MessageType.TextWithCitations;
  text: string;
  citations?: CitationInfo[] | null;
}

/**
 * NotifyMessage matching C# NotifyMessage.cs.
 *
 * An out-of-band notification pushed into a running conversation from an asynchronous source
 * (async sub-agent completion, context discovery, monitors, timers/cron). It maps to a user-role
 * message for the LLM whose {@link text} is a self-describing envelope naming the originating tool
 * call, but renders as a distinct notification pill in the UI (never a user bubble). The structured
 * fields carry snake_case wire names (matching the C# `[JsonPropertyName]` attributes) so the client
 * can render the pill without parsing the envelope text.
 */
export interface NotifyMessage extends IMessage {
  $type: typeof MessageType.Notify;
  /** The rendered envelope the LLM reads (computed on the backend from the structured fields). */
  text: string;
  /** Discriminating kind of notification, e.g. 'subagent-completion' | 'context-discovery'. */
  notify_kind: string;
  /** Id of the tool call this notification responds to, if any (omitted for timer/cron/context). */
  source_tool_call_id?: string | null;
  /** Name of the tool call this notification responds to, if any. */
  source_tool_name?: string | null;
  /** Short human/UI label (e.g. sub-agent template name, discovered file path). */
  label?: string | null;
  /** Pre-rendered payload body dropped verbatim into the envelope. Opaque — do not parse. */
  detail?: string | null;
}

/**
 * What one agent is saying to another — the CLOSED set matching C# `AgentMessageType`. The values are
 * the enum member names verbatim (`JsonStringEnumConverter` with no naming policy), so they arrive
 * PascalCase on the wire, unlike the lower-case `msg_type` the model writes when calling SendMessage.
 */
export type AgentMessageType = 'Question' | 'DelegateTask' | 'TaskUpdate' | 'Steer' | 'Response';

/**
 * AgentMessage matching C# AgentMessage.cs (#244).
 *
 * A message from one agent to another inside a collaboration. Like {@link NotifyMessage} it maps to a
 * user-role message for the LLM — {@link text} is the self-describing envelope naming the sender —
 * while the structured snake_case fields (mirroring the C# `[JsonPropertyName]` attributes) let the UI
 * render it as its own pill without parsing that envelope. It therefore must NEVER render as a user
 * bubble: `role` defaults to `'user'` on the backend, so every consumer has to test for this type
 * BEFORE it branches on role.
 */
export interface AgentMessage extends IMessage {
  $type: typeof MessageType.Agent;
  /** The rendered envelope the receiving LLM reads (computed on the backend from the fields below). */
  text: string;
  /** Collaboration-minted id of this message; the correlation key a reply carries in `in_response_to`. */
  message_id: string;
  /** What kind of message this is, and hence whether an answer is expected. */
  agent_message_type: AgentMessageType;
  /** Stable id of the sending agent — what a reply is addressed to, since names can collide. */
  from_agent_id: string;
  /** Human-facing name of the sending agent. */
  from_name: string;
  /** The message this one answers, when it answers one. */
  in_response_to?: string | null;
  /** The model-authored payload. Opaque — do not parse. */
  body?: string | null;
}

/**
 * Normalized data driving the notification pill. `displayItems` produces this from a
 * {@link NotifyMessage}, an {@link AgentMessage}, or a legacy `context_discovery` {@link TextMessage},
 * so all three render through the one unified pill.
 */
export interface NotificationDisplayData {
  notifyKind: string;
  label?: string | null;
  sourceToolName?: string | null;
  sourceToolCallId?: string | null;
  detail?: string | null;
  text?: string | null;
  /** Legacy sandbox context-discovery file path (mirrors TextMessage.context_discovery.path). */
  contextPath?: string | null;
  /** Legacy sandbox context-discovery truncation flag. */
  contextTruncated?: boolean;
  /** Set only for the `agent-message` kind: which {@link AgentMessageType} the pill is showing. */
  agentMessageType?: AgentMessageType | null;
}

/**
 * ConversationUsageMessage matching C# ConversationUsageMessage.cs (#196).
 *
 * A live-only frame carrying the conversation-wide token totals (and cost, when known) folded across the
 * WHOLE conversation tree — the primary loop's own turns plus every sub-agent / workflow descendant. The
 * backend broadcasts it whenever the folded aggregate changes so the usage banner reflects descendant
 * spend live rather than only after a reload. The token fields are the pre-computed banner tuple (including
 * the per-model uncached-input normalization), so the client SETs the banner from them directly. Transient:
 * never persisted — the authoritative figure survives reload via `GET /conversations/{id}/usage`.
 */
export interface ConversationUsageMessage extends IMessage {
  $type: typeof MessageType.ConversationUsage;
  totalTokens: number;
  promptTokens: number;
  uncachedInputTokens: number;
  completionTokens: number;
  cachedTokens: number;
  cacheCreationTokens: number;
  /** 'InProgress' | 'Partial' | 'Complete'. */
  completeness: string;
  estimatedCostMicros?: number | null;
  providerReportedCostMicros?: number | null;
  currency?: string;
}

/**
 * ConversationTodoMessage matching C# ConversationTodoMessage.cs (#583).
 *
 * A live-only frame carrying the conversation's WHOLE ToDo board on every change — the mutating task
 * tools return short acks ("Added task 3: ..."), never the list, so the client cannot reconstruct the
 * board from tool results and needs this feed instead. Like the conversation-usage frame it is a
 * complete snapshot, so the client SETs the board from it rather than accumulating; the newest frame
 * is the whole truth. Transient: never persisted — the board survives reload via
 * `GET /conversations/{id}/todos`.
 *
 * `tasks` carries the `TaskItem` fields from #312 plus `artifacts` (#583, PR 5). PR 4 adds
 * assignee, blockedBy and timestamps; a client running ahead of its server simply does not see them.
 */
export interface ConversationTodoMessage extends IMessage {
  $type: typeof MessageType.ConversationTodo;
  /**
   * The conversation this board belongs to; lets a client drop a frame meant for another thread.
   *
   * REQUIRED, matching the producer. `ConversationTodoMessage.ThreadId` is a `required string` in C#
   * and `FromSnapshot` runs `ArgumentException.ThrowIfNullOrWhiteSpace` on it, so a frame with a
   * blank thread id is never emitted — the publish is skipped and logged instead. Declaring this
   * optional would be worse than inaccurate: `useTodoBoard` fails CLOSED on a missing id, so a
   * consumer written against `threadId?` would be coding for a case that silently blanks the board
   * and cannot actually occur. The runtime guard still checks it, because the wire is untrusted.
   */
  threadId: string;
  tasks: TodoTask[];
}

/**
 * ContextPressureMessage matching C# ContextPressureMessage.cs (#681; spec 679 §7.2).
 *
 * A live-only frame carrying one agent loop's latest context observation: how full the model's window
 * is, measured or estimated, for the thread it was taken on. Published after each observation write,
 * and ONLY when the model's window is known (a gauge with no scale shows nothing). Transient: never
 * persisted — the authoritative figure survives reload via `GET /conversations/{id}/context`, and
 * frames only update that snapshot, never replace it (the usage-frame rule).
 *
 * Content-free by construction: counts, ratios, ids and statuses only. Field names are pinned to
 * camelCase by `JsonPropertyName` on the producer, independent of the serializer's naming policy.
 */
export interface ContextPressureMessage extends IMessage {
  $type: typeof MessageType.ContextPressure;
  /** The thread the observation was taken on. Optional on the wire; the consumer fails closed without it. */
  threadId?: string | null;
  /** `root` or the sub-agent id. */
  agentId: string;
  generationOrdinal: number;
  observedAtUtc: string;
  effectiveModelId: string;
  estimatedInputTokens: number;
  measuredInputTokens?: number | null;
  /** Measured | Estimated | Unavailable. */
  provenance: string;
  windowTokens?: number | null;
  reserveTokens: number;
  /** Fraction of the usable window the request occupies; absent when the window is unknown. */
  utilization?: number | null;
  activeCheckpointId?: string | null;
  rowsInView?: number;
}

/**
 * Union type for all message types
 */
export type Message =
  | TextMessage
  | TextUpdateMessage
  | ImageMessage
  | ToolsCallMessage
  | ToolsCallUpdateMessage
  | ToolCallMessage
  | ToolCallUpdateMessage
  | ToolCallResultMessage
  | ToolsCallResultMessage
  | UsageMessage
  | ReasoningMessage
  | ReasoningUpdateMessage
  | RunAssignmentMessage
  | RunCompletedMessage
  | ServerToolUseMessage
  | ServerToolResultMessage
  | TextWithCitationsMessage
  | NotifyMessage
  | AgentMessage
  | ConversationUsageMessage
  | ConversationTodoMessage
  | ContextPressureMessage;

// Type guard functions

export function isTextMessage(msg: IMessage): msg is TextMessage {
  return msg.$type === MessageType.Text;
}

export function isTextUpdateMessage(msg: IMessage): msg is TextUpdateMessage {
  return msg.$type === MessageType.TextUpdate;
}

export function isImageMessage(msg: IMessage): msg is ImageMessage {
  return msg.$type === MessageType.Image;
}

export function isToolsCallMessage(msg: IMessage): msg is ToolsCallMessage {
  return msg.$type === MessageType.ToolsCall;
}

export function isToolsCallUpdateMessage(msg: IMessage): msg is ToolsCallUpdateMessage {
  return msg.$type === MessageType.ToolsCallUpdate;
}

export function isToolCallResultMessage(msg: IMessage): msg is ToolCallResultMessage {
  return msg.$type === MessageType.ToolCallResult;
}

export function isUsageMessage(msg: IMessage): msg is UsageMessage {
  return msg.$type === MessageType.Usage;
}

export function isReasoningMessage(msg: IMessage): msg is ReasoningMessage {
  return msg.$type === MessageType.Reasoning;
}

export function isReasoningUpdateMessage(msg: IMessage): msg is ReasoningUpdateMessage {
  return msg.$type === MessageType.ReasoningUpdate;
}

export function isRunAssignmentMessage(msg: IMessage): msg is RunAssignmentMessage {
  return msg.$type === MessageType.RunAssignment;
}

export function isRunCompletedMessage(msg: IMessage): msg is RunCompletedMessage {
  return msg.$type === MessageType.RunCompleted;
}

export function isToolCallMessage(msg: IMessage): msg is ToolCallMessage {
  return msg.$type === MessageType.ToolCall;
}

export function isToolCallUpdateMessage(msg: IMessage): msg is ToolCallUpdateMessage {
  return msg.$type === MessageType.ToolCallUpdate;
}

export function isServerToolUseMessage(msg: IMessage): msg is ServerToolUseMessage {
  return (
    msg.$type === MessageType.ServerToolUse ||
    (msg.$type === MessageType.ToolCall &&
      (msg as ToolCallMessage).execution_target === 'ProviderServer')
  );
}

export function isServerToolResultMessage(msg: IMessage): msg is ServerToolResultMessage {
  return (
    msg.$type === MessageType.ServerToolResult ||
    (msg.$type === MessageType.ToolCallResult &&
      (msg as ToolCallResultMessage).execution_target === 'ProviderServer')
  );
}

export function isTextWithCitationsMessage(msg: IMessage): msg is TextWithCitationsMessage {
  return msg.$type === MessageType.TextWithCitations;
}

export function isNotifyMessage(msg: IMessage): msg is NotifyMessage {
  return msg.$type === MessageType.Notify;
}

export function isAgentMessage(msg: IMessage): msg is AgentMessage {
  return msg.$type === MessageType.Agent;
}

export function isConversationUsageMessage(msg: IMessage): msg is ConversationUsageMessage {
  return msg.$type === MessageType.ConversationUsage;
}

export function isConversationTodoMessage(msg: IMessage): msg is ConversationTodoMessage {
  return msg.$type === MessageType.ConversationTodo;
}

export function isContextPressureMessage(msg: IMessage): msg is ContextPressureMessage {
  return msg.$type === MessageType.ContextPressure;
}

/**
 * Check if a message is a streaming update (not final)
 */
export function isUpdateMessage(msg: IMessage): boolean {
  return (
    isTextUpdateMessage(msg) ||
    isToolsCallUpdateMessage(msg) ||
    isToolCallUpdateMessage(msg) ||
    isReasoningUpdateMessage(msg)
  );
}

/**
 * Check if a message is a lifecycle message
 */
export function isLifecycleMessage(msg: IMessage): boolean {
  return isRunAssignmentMessage(msg) || isRunCompletedMessage(msg);
}

/**
 * Display item types for rendering the chat UI
 */
export type DisplayItem =
  | { type: 'user-message'; id: string; content: TextMessage; status: 'pending' | 'active' | 'completed'; timestamp: number }
  | { type: 'assistant-message'; id: string; content: TextMessage; runId?: string | null; parentRunId?: string | null; messageOrderIdx?: number | null }
  | { type: 'pill'; id: string; items: Array<ReasoningMessage | ToolsCallMessage>; runId?: string | null; parentRunId?: string | null; messageOrderIdx?: number | null }
  | { type: 'notification'; id: string; notification: NotificationDisplayData; runId?: string | null };

/**
 * Status for tracking message lifecycle
 */
export type MessageStatus = 'pending' | 'active' | 'completed';
