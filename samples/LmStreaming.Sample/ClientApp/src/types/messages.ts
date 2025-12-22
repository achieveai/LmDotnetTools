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
} as const;

export type MessageTypeValue = (typeof MessageType)[keyof typeof MessageType];

/**
 * Role enum matching C# Role enum
 */
export type Role = 'none' | 'user' | 'assistant' | 'system' | 'tool';

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
  result: string;
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

/**
 * ReasoningMessage matching C# ReasoningMessage.cs
 */
export interface ReasoningMessage extends IMessage {
  $type: typeof MessageType.Reasoning;
  reasoning: string;
  visibility?: ReasoningVisibility;
}

/**
 * ReasoningUpdateMessage matching C# ReasoningUpdateMessage.cs
 */
export interface ReasoningUpdateMessage extends IMessage {
  $type: typeof MessageType.ReasoningUpdate;
  reasoning: string;
  isUpdate: true;
  visibility?: ReasoningVisibility | null;
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
}

/**
 * ToolCallMessage matching C# ToolCallMessage.cs (individual tool call, not aggregate)
 */
export interface ToolCallMessage extends IMessage {
  $type: typeof MessageType.ToolCall;
  tool_call_id?: string | null;
  function_name?: string | null;
  function_args?: string | null;
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
  | RunCompletedMessage;

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