import type {
  ServerToolUseMessage,
  ServerToolResultMessage,
  TextWithCitationsMessage,
  ToolsCallMessage,
  ToolCallResultMessage,
  TextMessage,
  ExecutionTarget,
} from '@/types';
import { MessageType } from '@/types';

/**
 * Provider-side (server-tool / citation) message shapes are not renderable by the display pipeline
 * directly: the pill/text grouping in {@link buildDisplayItems} only understands the canonical
 * ToolsCall / ToolCallResult / Text shapes. These pure converters map each provider-side frame onto
 * its canonical equivalent so every consumer of the stream (the parent chat AND the sub-agent panel)
 * renders and merges them identically instead of each re-deriving the mapping. Keeping the mapping in
 * one place is what guarantees a focused child transcript stays at parity with the parent transcript.
 */

/**
 * Map a `server_tool_use` frame onto a single-call {@link ToolsCallMessage} so it groups into the
 * tool pill exactly like a locally-executed tool call. Accepts both the legacy shape
 * (`tool_use_id` / `tool_name` / `input`) and the unified shape (`tool_call_id` / `function_name` /
 * `function_args`); the tool call carries `tool_call_id` so its result can later resolve the pill.
 */
export function serverToolUseToToolsCall(msg: ServerToolUseMessage): ToolsCallMessage {
  const stUse = msg as unknown as Record<string, unknown>;
  const toolName =
    (typeof stUse.tool_name === 'string' ? stUse.tool_name : undefined)
    ?? (typeof stUse.function_name === 'string' ? stUse.function_name : undefined);
  const toolUseId =
    (typeof stUse.tool_use_id === 'string' ? stUse.tool_use_id : undefined)
    ?? (typeof stUse.tool_call_id === 'string' ? stUse.tool_call_id : undefined);
  const rawInput = stUse.input ?? stUse.function_args ?? {};
  const executionTarget: ExecutionTarget | undefined =
    stUse.execution_target === 'ProviderServer' || stUse.execution_target === 'LocalFunction'
      ? stUse.execution_target
      : undefined;
  const inputStr = typeof rawInput === 'string' ? rawInput : JSON.stringify(rawInput ?? {});
  return {
    $type: MessageType.ToolsCall,
    tool_calls: [{
      function_name: toolName,
      function_args: inputStr,
      tool_call_id: toolUseId,
      execution_target: executionTarget,
    }],
    role: msg.role,
    fromAgent: msg.fromAgent,
    generationId: msg.generationId,
    runId: msg.runId,
    parentRunId: msg.parentRunId,
    threadId: msg.threadId,
    messageOrderIdx: msg.messageOrderIdx,
  };
}

/**
 * Map a `server_tool_result` frame onto a {@link ToolCallResultMessage} keyed by the originating
 * tool id, so it attaches to its tool-use pill just like a local tool result. Accepts the legacy
 * (`tool_use_id` / `is_error` / `error_code`) and unified (`tool_call_id` / `isError` / `errorCode`)
 * shapes; an error result is flattened into a human-readable string prefixed with its error code.
 */
export function serverToolResultToToolCallResult(msg: ServerToolResultMessage): ToolCallResultMessage {
  const stResult = msg as unknown as Record<string, unknown>;
  const toolUseId =
    (typeof stResult.tool_use_id === 'string' ? stResult.tool_use_id : undefined)
    ?? (typeof stResult.tool_call_id === 'string' ? stResult.tool_call_id : undefined);
  const toolName =
    (typeof stResult.tool_name === 'string' ? stResult.tool_name : undefined)
    ?? (typeof stResult.function_name === 'string' ? stResult.function_name : undefined);
  const isError =
    (typeof stResult.is_error === 'boolean' ? stResult.is_error : undefined)
    ?? (typeof stResult.isError === 'boolean' ? stResult.isError : undefined)
    ?? false;
  const errorCode =
    (typeof stResult.error_code === 'string' ? stResult.error_code : undefined)
    ?? (typeof stResult.errorCode === 'string' ? stResult.errorCode : undefined)
    ?? null;
  const rawResult = stResult.result;
  const resultStr = typeof rawResult === 'string' ? rawResult : JSON.stringify(rawResult ?? {});
  return {
    $type: MessageType.ToolCallResult,
    tool_call_id: toolUseId,
    tool_name: toolName,
    result: isError ? `Error (${errorCode || 'unknown'}): ${resultStr}` : resultStr,
    is_error: isError,
    error_code: errorCode,
    role: msg.role,
    generationId: msg.generationId,
    runId: msg.runId,
    parentRunId: msg.parentRunId,
    threadId: msg.threadId,
    messageOrderIdx: msg.messageOrderIdx,
  };
}

/**
 * Map a `text_with_citations` frame onto a plain {@link TextMessage}, folding the distinct citation
 * URLs into an appended markdown "Sources" list so the answer bubble renders the same rich text the
 * parent chat shows. Deduplicates by URL to avoid repeating the same source link.
 */
export function textWithCitationsToText(msg: TextWithCitationsMessage): TextMessage {
  let text = msg.text;
  if (msg.citations?.length) {
    const uniqueUrls = new Map<string, { title: string; url: string }>();
    for (const cite of msg.citations) {
      if (cite.url && !uniqueUrls.has(cite.url)) {
        uniqueUrls.set(cite.url, {
          title: cite.title || cite.url,
          url: cite.url,
        });
      }
    }
    if (uniqueUrls.size > 0) {
      text += '\n\n**Sources:**\n';
      for (const { title, url } of uniqueUrls.values()) {
        text += `- [${title}](${url})\n`;
      }
    }
  }
  return {
    $type: MessageType.Text,
    text,
    role: msg.role,
    fromAgent: msg.fromAgent,
    generationId: msg.generationId,
    runId: msg.runId,
    parentRunId: msg.parentRunId,
    threadId: msg.threadId,
    messageOrderIdx: msg.messageOrderIdx,
  };
}
