import { describe, it, expect } from 'vitest';
import {
  AGENT_HUES,
  MAIN_TAB_COLOR,
  hueForIndex,
  resolveAgentIdFromCall,
  resolveAgentRoutingFromCall,
} from '@/utils/agentColors';

describe('agentColors palette', () => {
  it('exposes a non-empty palette of distinct hues', () => {
    expect(AGENT_HUES.length).toBeGreaterThan(1);
    expect(new Set(AGENT_HUES).size).toBe(AGENT_HUES.length);
  });

  it('does not reuse the app reserved accent/error colors', () => {
    expect(AGENT_HUES).not.toContain('#007bff');
    expect(AGENT_HUES).not.toContain('#dc3545');
    expect(MAIN_TAB_COLOR).not.toBe('#007bff');
  });

  it('assigns hues by discovery index and wraps around the palette', () => {
    expect(hueForIndex(0)).toBe(AGENT_HUES[0]);
    expect(hueForIndex(1)).toBe(AGENT_HUES[1]);
    // Wrap: index N returns hue 0 again.
    expect(hueForIndex(AGENT_HUES.length)).toBe(AGENT_HUES[0]);
    expect(hueForIndex(AGENT_HUES.length + 2)).toBe(AGENT_HUES[2]);
  });
});

describe('resolveAgentRoutingFromCall (exact child only)', () => {
  const children = [
    {
      agentId: 'child-1',
      name: 'parallel:1:parallel:task1',
      template: 'code-reviewer:architecture-review',
      task: 'review',
      status: 'completed' as const,
      threadId: 'subagent-child-1',
      effectiveModelId: 'claude-opus-5',
      effectiveModelIntelligence: 5,
      modelSelectionSource: 'template-tier',
    },
  ];

  it('correlates a synchronous workflow Agent call by exact unit name', () => {
    expect(
      resolveAgentRoutingFromCall(
        { name: 'parallel:1:parallel:task1', subagent_type: 'code-reviewer:architecture-review' },
        'MIR-tier5',
        children
      )
    ).toEqual(children[0]);
  });

  it('never guesses routing from subagent_type alone', () => {
    expect(
      resolveAgentRoutingFromCall(
        { subagent_type: 'code-reviewer:architecture-review' },
        'MIR-tier5',
        children
      )
    ).toBeNull();
  });
});

describe('resolveAgentIdFromCall (exact-agentId only)', () => {
  it('reads agent_id from call args (sendmessage/checkagent)', () => {
    expect(resolveAgentIdFromCall({ agent_id: 'a-42' }, null)).toBe('a-42');
    expect(resolveAgentIdFromCall({ agentId: 'a-7' }, null)).toBe('a-7');
  });

  it('reads agent_id from a background spawn result JSON when args lack it', () => {
    const spawnResult = JSON.stringify({ agent_id: 'spawned-9', name: null, template: 'research', status: 'spawned' });
    expect(resolveAgentIdFromCall({ subagent_type: 'research', prompt: 'go' }, spawnResult)).toBe('spawned-9');
  });

  it('prefers args agent_id over the result', () => {
    const result = JSON.stringify({ agent_id: 'from-result' });
    expect(resolveAgentIdFromCall({ agent_id: 'from-args' }, result)).toBe('from-args');
  });

  it('returns null when no exact id is available (synchronous spawn answer text, or template-only args)', () => {
    expect(resolveAgentIdFromCall({ subagent_type: 'research', prompt: 'go' }, 'The answer is 42.')).toBeNull();
    expect(resolveAgentIdFromCall({ subagent_type: 'research' }, null)).toBeNull();
    expect(resolveAgentIdFromCall(null, null)).toBeNull();
  });
});
