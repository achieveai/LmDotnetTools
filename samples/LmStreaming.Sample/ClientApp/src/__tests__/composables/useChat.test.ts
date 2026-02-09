import { describe, it, expect } from 'vitest';
import { isTestInstruction, getDisplayText, convertServerToolUse, convertServerToolResult } from '@/composables/useChat';
import { MessageType } from '@/types';
import type { ServerToolUseMessage, ServerToolResultMessage } from '@/types';

describe('isTestInstruction', () => {
  it('should return true for messages with both start and end markers', () => {
    const text = '<|instruction_start|>{"instruction_chain": []}<|instruction_end|>';
    expect(isTestInstruction(text)).toBe(true);
  });

  it('should return true for complex instruction with JSON content', () => {
    const text = '<|instruction_start|>{"instruction_chain":[{"type":"test","content":"hello"}]}<|instruction_end|>';
    expect(isTestInstruction(text)).toBe(true);
  });

  it('should return false for messages without markers', () => {
    const text = 'Hello, this is a normal message';
    expect(isTestInstruction(text)).toBe(false);
  });

  it('should return false for messages with only start marker', () => {
    const text = '<|instruction_start|> some content without end';
    expect(isTestInstruction(text)).toBe(false);
  });

  it('should return false for messages with only end marker', () => {
    const text = 'some content without start <|instruction_end|>';
    expect(isTestInstruction(text)).toBe(false);
  });

  it('should return false for empty string', () => {
    expect(isTestInstruction('')).toBe(false);
  });
});

describe('getDisplayText', () => {
  it('should return emoji text for instruction messages', () => {
    const text = '<|instruction_start|>{"instruction_chain": []}<|instruction_end|>';
    expect(getDisplayText(text)).toBe('🧪 Test instruction sent');
  });

  it('should return original text for normal messages', () => {
    const text = 'Hello, this is a normal message';
    expect(getDisplayText(text)).toBe('Hello, this is a normal message');
  });

  it('should return original text for empty string', () => {
    expect(getDisplayText('')).toBe('');
  });

  it('should return original text for messages with partial markers', () => {
    const text = '<|instruction_start|> incomplete instruction';
    expect(getDisplayText(text)).toBe('<|instruction_start|> incomplete instruction');
  });
});

describe('convertServerToolUse', () => {
  it('should convert ServerToolUseMessage to ToolsCallMessage with correct structure', () => {
    const serverToolUse: ServerToolUseMessage = {
      $type: MessageType.ServerToolUse,
      tool_use_id: 'srvtoolu_01ABC',
      tool_name: 'web_search',
      input: { query: 'current weather' },
      role: 'assistant',
      generationId: 'gen_123',
    };

    const result = convertServerToolUse(serverToolUse);

    expect(result.$type).toBe(MessageType.ToolsCall);
    expect(result.tool_calls).toHaveLength(1);
    expect(result.tool_calls![0].function_name).toBe('web_search');
    expect(result.tool_calls![0].tool_call_id).toBe('srvtoolu_01ABC');
    expect(JSON.parse(result.tool_calls![0].function_args!)).toEqual({ query: 'current weather' });
    expect(result.role).toBe('assistant');
    expect(result.generationId).toBe('gen_123');
  });

  it('should handle empty input object', () => {
    const serverToolUse: ServerToolUseMessage = {
      $type: MessageType.ServerToolUse,
      tool_use_id: 'srvtoolu_synth_1',
      tool_name: 'web_search',
      input: {},
      role: 'assistant',
    };

    const result = convertServerToolUse(serverToolUse);

    expect(result.tool_calls![0].function_args).toBe('{}');
    expect(result.tool_calls![0].tool_call_id).toBe('srvtoolu_synth_1');
  });

  it('should handle undefined input', () => {
    const serverToolUse: ServerToolUseMessage = {
      $type: MessageType.ServerToolUse,
      tool_use_id: 'srvtoolu_02',
      tool_name: 'web_fetch',
      role: 'assistant',
    };

    const result = convertServerToolUse(serverToolUse);

    expect(result.tool_calls![0].function_args).toBe('{}');
    expect(result.tool_calls![0].function_name).toBe('web_fetch');
  });

  it('should preserve all metadata fields', () => {
    const serverToolUse: ServerToolUseMessage = {
      $type: MessageType.ServerToolUse,
      tool_use_id: 'srvtoolu_03',
      tool_name: 'web_search',
      input: { query: 'test' },
      role: 'assistant',
      fromAgent: 'agent_1',
      generationId: 'gen_456',
      runId: 'run_789',
      parentRunId: 'run_000',
      threadId: 'thread_123',
      messageOrderIdx: 2,
    };

    const result = convertServerToolUse(serverToolUse);

    expect(result.fromAgent).toBe('agent_1');
    expect(result.generationId).toBe('gen_456');
    expect(result.runId).toBe('run_789');
    expect(result.parentRunId).toBe('run_000');
    expect(result.threadId).toBe('thread_123');
    expect(result.messageOrderIdx).toBe(2);
  });
});

describe('convertServerToolResult', () => {
  it('should convert ServerToolResultMessage to ToolCallResultMessage', () => {
    const serverToolResult: ServerToolResultMessage = {
      $type: MessageType.ServerToolResult,
      tool_use_id: 'srvtoolu_01ABC',
      tool_name: 'web_search',
      result: [{ title: 'Result 1', url: 'https://example.com' }],
      role: 'assistant',
      generationId: 'gen_123',
    };

    const result = convertServerToolResult(serverToolResult);

    expect(result.$type).toBe(MessageType.ToolCallResult);
    expect(result.tool_call_id).toBe('srvtoolu_01ABC');
    expect(JSON.parse(result.result)).toEqual([{ title: 'Result 1', url: 'https://example.com' }]);
    expect(result.role).toBe('assistant');
  });

  it('should handle error results', () => {
    const serverToolResult: ServerToolResultMessage = {
      $type: MessageType.ServerToolResult,
      tool_use_id: 'srvtoolu_01',
      tool_name: 'web_search',
      result: 'rate limit exceeded',
      is_error: true,
      error_code: 'rate_limit',
      role: 'assistant',
    };

    const result = convertServerToolResult(serverToolResult);

    expect(result.result).toBe('Error (rate_limit): rate limit exceeded');
    expect(result.tool_call_id).toBe('srvtoolu_01');
  });

  it('should handle error with no error_code', () => {
    const serverToolResult: ServerToolResultMessage = {
      $type: MessageType.ServerToolResult,
      tool_use_id: 'srvtoolu_01',
      tool_name: 'web_search',
      result: 'something failed',
      is_error: true,
      role: 'assistant',
    };

    const result = convertServerToolResult(serverToolResult);

    expect(result.result).toBe('Error (unknown): something failed');
  });

  it('should handle empty result', () => {
    const serverToolResult: ServerToolResultMessage = {
      $type: MessageType.ServerToolResult,
      tool_use_id: 'srvtoolu_synth_1',
      tool_name: 'web_search',
      role: 'assistant',
    };

    const result = convertServerToolResult(serverToolResult);

    expect(result.result).toBe('{}');
    expect(result.tool_call_id).toBe('srvtoolu_synth_1');
  });

  // Regression: synthetic IDs from Kimi (no id on server_tool_use) must round-trip
  it('should preserve synthetic tool_use_id for result linkage', () => {
    const syntheticId = 'srvtoolu_synth_1_abc123';
    const serverToolResult: ServerToolResultMessage = {
      $type: MessageType.ServerToolResult,
      tool_use_id: syntheticId,
      tool_name: 'web_search',
      result: [],
      role: 'assistant',
    };

    const result = convertServerToolResult(serverToolResult);
    expect(result.tool_call_id).toBe(syntheticId);
  });
});

// Regression test: converted ServerToolUse and ServerToolResult must share the same tool_call_id for UI linkage
describe('server tool persistence round-trip', () => {
  it('should produce matching tool_call_id between converted use and result', () => {
    const toolUseId = 'srvtoolu_synth_1_test';

    const useMsg: ServerToolUseMessage = {
      $type: MessageType.ServerToolUse,
      tool_use_id: toolUseId,
      tool_name: 'web_search',
      input: { query: 'hello' },
      role: 'assistant',
    };

    const resultMsg: ServerToolResultMessage = {
      $type: MessageType.ServerToolResult,
      tool_use_id: toolUseId,
      tool_name: 'web_search',
      result: [{ title: 'Search result' }],
      role: 'assistant',
    };

    const convertedUse = convertServerToolUse(useMsg);
    const convertedResult = convertServerToolResult(resultMsg);

    // The converted use's tool_call_id must match the result's tool_call_id
    expect(convertedUse.tool_calls![0].tool_call_id).toBe(convertedResult.tool_call_id);
  });
});
