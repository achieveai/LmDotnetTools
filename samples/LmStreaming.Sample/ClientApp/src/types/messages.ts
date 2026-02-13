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
  | TextWithCitationsMessage;

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
  | { type: 'pill'; id: string; items: Array<ReasoningMessage | ToolsCallMessage>; runId?: string | null; parentRunId?: string | null; messageOrderIdx?: number | null };

/**
 * Status for tracking message lifecycle
 */
export type MessageStatus = 'pending' | 'active' | 'completed';
