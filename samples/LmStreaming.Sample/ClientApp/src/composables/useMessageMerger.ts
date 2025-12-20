import { ref } from 'vue';
import {
  type Message,
  type TextMessage,
  type TextUpdateMessage,
  type ToolsCallMessage,
  type ToolsCallUpdateMessage,
  type ToolCallMessage,
  type ToolCallUpdateMessage,
  type ToolCall,
  type ReasoningMessage,
  type ReasoningUpdateMessage,
  type Role,
  type ReasoningVisibility,
  MessageType,
  isTextUpdateMessage,
  isToolsCallUpdateMessage,
  isToolCallUpdateMessage,
  isToolCallMessage,
  isToolCallResultMessage,
  isReasoningUpdateMessage,
} from '@/types';

/**
 * Represents a message being accumulated from streaming updates
 */
interface AccumulatingMessage {
  type: 'text' | 'tools_call' | 'tool_call' | 'reasoning';
  generationId?: string | null;
  text?: string;
  isThinking?: boolean;
  role: Role;
  toolCalls?: ToolCall[];
  currentToolCall?: Partial<ToolCall>;
  reasoning?: string;
  visibility?: ReasoningVisibility;
  // For individual tool call accumulation
  toolCallId?: string | null;
  functionName?: string | null;
  functionArgs?: string;
}

/**
 * Composable for merging streaming message updates into complete messages.
 * Implements the same accumulation logic as C# builders:
 * - TextMessageBuilder
 * - ToolsCallMessageBuilder
 * - ReasoningMessageBuilder
 */
export function useMessageMerger() {
  // Map of generationId -> accumulating message
  const accumulators = ref<Map<string, AccumulatingMessage>>(new Map());

  /**
   * Process an incoming streaming update and return merged message.
   * For update messages, returns the accumulated state.
   * For non-update messages, returns them directly.
   */
  function processUpdate(message: Message): Message {
    const genId = message.generationId || 'default';

    if (isTextUpdateMessage(message)) {
      return processTextUpdate(genId, message);
    }

    if (isToolsCallUpdateMessage(message)) {
      return processToolsCallUpdate(genId, message);
    }

    if (isToolCallUpdateMessage(message)) {
      return processToolCallUpdate(genId, message);
    }

    if (isReasoningUpdateMessage(message)) {
      return processReasoningUpdate(genId, message);
    }

    // Tool call messages and tool call result messages pass through directly
    // but could be used to display tool state in UI
    if (isToolCallMessage(message) || isToolCallResultMessage(message)) {
      return message;
    }

    // Non-update messages pass through directly
    return message;
  }

  /**
   * Process a TextUpdateMessage and return accumulated TextMessage
   */
  function processTextUpdate(genId: string, update: TextUpdateMessage): TextMessage {
    let acc = accumulators.value.get(genId);

    if (!acc || acc.type !== 'text') {
      acc = {
        type: 'text',
        generationId: update.generationId,
        text: '',
        isThinking: update.isThinking,
        role: update.role,
      };
      accumulators.value.set(genId, acc);
    }

    // Concatenate text updates: each update is a delta chunk that should be appended
    // The server sends sequential chunks with incrementing chunkIdx
    acc.text = (acc.text || '') + update.text;
    acc.isThinking = update.isThinking;

    return {
      $type: MessageType.Text,
      text: acc.text || '',
      isThinking: acc.isThinking,
      role: acc.role,
      generationId: acc.generationId,
    };
  }

  /**
   * Process a ToolsCallUpdateMessage and return accumulated ToolsCallMessage
   */
  function processToolsCallUpdate(
    genId: string,
    update: ToolsCallUpdateMessage
  ): ToolsCallMessage {
    let acc = accumulators.value.get(genId);

    if (!acc || acc.type !== 'tools_call') {
      acc = {
        type: 'tools_call',
        generationId: update.generationId,
        role: update.role,
        toolCalls: [],
        currentToolCall: {},
      };
      accumulators.value.set(genId, acc);
    }

    // Process each tool call update (matching ToolsCallMessageBuilder logic)
    for (const tcUpdate of update.tool_call_updates) {
      // Check if this is a new tool call
      const isNewToolCall =
        (acc.currentToolCall?.tool_call_id &&
          tcUpdate.tool_call_id &&
          acc.currentToolCall.tool_call_id !== tcUpdate.tool_call_id) ||
        (acc.currentToolCall?.index !== undefined &&
          tcUpdate.index !== undefined &&
          acc.currentToolCall.index !== tcUpdate.index);

      if (isNewToolCall && acc.currentToolCall?.function_name) {
        // Complete current tool call
        acc.toolCalls!.push(acc.currentToolCall as ToolCall);
        acc.currentToolCall = {};
      }

      // Start new or update current tool call
      if (tcUpdate.function_name && !acc.currentToolCall?.function_name) {
        acc.currentToolCall = {
          function_name: tcUpdate.function_name,
          function_args: tcUpdate.function_args || '',
          tool_call_id: tcUpdate.tool_call_id,
          index: tcUpdate.index,
        };
      } else if (acc.currentToolCall?.function_name && tcUpdate.function_args) {
        acc.currentToolCall.function_args =
          (acc.currentToolCall.function_args || '') + tcUpdate.function_args;

        if (!acc.currentToolCall.tool_call_id && tcUpdate.tool_call_id) {
          acc.currentToolCall.tool_call_id = tcUpdate.tool_call_id;
        }
      }
    }

    // Return current accumulated state
    const allToolCalls = [...(acc.toolCalls || [])];
    if (acc.currentToolCall?.function_name) {
      allToolCalls.push(acc.currentToolCall as ToolCall);
    }

    return {
      $type: MessageType.ToolsCall,
      tool_calls: allToolCalls,
      role: acc.role,
      generationId: acc.generationId,
    };
  }

  /**
   * Process a ToolCallUpdateMessage (individual tool call streaming) and return accumulated ToolCallMessage
   */
  function processToolCallUpdate(
    genId: string,
    update: ToolCallUpdateMessage
  ): ToolCallMessage {
    // Use tool_call_id as key to support multiple concurrent tool calls
    const toolCallKey = `${genId}-${update.tool_call_id || 'tc'}`;
    let acc = accumulators.value.get(toolCallKey);

    if (!acc || acc.type !== 'tool_call') {
      acc = {
        type: 'tool_call',
        generationId: update.generationId,
        role: update.role,
        toolCallId: update.tool_call_id,
        functionName: update.function_name,
        functionArgs: '',
      };
      accumulators.value.set(toolCallKey, acc);
    }

    // Set function name if provided (usually first chunk)
    if (update.function_name && !acc.functionName) {
      acc.functionName = update.function_name;
    }

    // Accumulate function arguments
    if (update.function_args) {
      acc.functionArgs = (acc.functionArgs || '') + update.function_args;
    }

    return {
      $type: MessageType.ToolCall,
      tool_call_id: acc.toolCallId,
      function_name: acc.functionName,
      function_args: acc.functionArgs,
      role: acc.role,
      generationId: acc.generationId,
    };
  }

  /**
   * Process a ReasoningUpdateMessage and return accumulated ReasoningMessage
   */
  function processReasoningUpdate(
    genId: string,
    update: ReasoningUpdateMessage
  ): ReasoningMessage {
    let acc = accumulators.value.get(genId);

    if (!acc || acc.type !== 'reasoning') {
      acc = {
        type: 'reasoning',
        generationId: update.generationId,
        reasoning: '',
        role: update.role,
        visibility: update.visibility || 'Plain',
      };
      accumulators.value.set(genId, acc);
    }

    // Accumulate reasoning text
    acc.reasoning = (acc.reasoning || '') + update.reasoning;

    return {
      $type: MessageType.Reasoning,
      reasoning: acc.reasoning || '',
      visibility: acc.visibility,
      role: acc.role,
      generationId: acc.generationId,
    };
  }

  /**
   * Finalize and clear accumulator for a generation
   */
  function finalize(generationId?: string): void {
    const genId = generationId || 'default';
    accumulators.value.delete(genId);
  }

  /**
   * Clear all accumulators
   */
  function reset(): void {
    accumulators.value.clear();
  }

  return {
    processUpdate,
    finalize,
    reset,
  };
}
