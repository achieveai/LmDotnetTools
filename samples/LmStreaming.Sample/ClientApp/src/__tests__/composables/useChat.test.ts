import { describe, it, expect } from 'vitest';
import { isTestInstruction, getDisplayText } from '@/composables/useChat';

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
