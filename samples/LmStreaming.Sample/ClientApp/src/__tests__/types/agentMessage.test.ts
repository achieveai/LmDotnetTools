import { describe, it, expect } from 'vitest';
import fs from 'fs';
import path from 'path';
import { MessageType, isAgentMessage, isNotifyMessage, isTextMessage } from '@/types';
import type { AgentMessage, IMessage } from '@/types';

/**
 * The client's view of an agent-to-agent message (#244).
 *
 * Two things are pinned here. First the GUARD, because `AgentMessage.role` is `user` on the wire:
 * every consumer must be able to recognize the type BEFORE it branches on role, or an agent's words
 * render as the human's. Second the PARITY with the C# records, by scanning their source: these are
 * hand-mirrored types with no codegen between them, so a field added on the server would otherwise
 * be invisible here until someone noticed a blank pill.
 */
const REPO_ROOT = path.resolve(__dirname, '../../../../../..');
const AGENT_MESSAGE_CS = path.join(REPO_ROOT, 'src/LmCore/Messages/AgentMessage.cs');
const SUB_AGENT_SUMMARY_CS = path.join(
  REPO_ROOT,
  'samples/LmStreaming.Sample/Models/SubAgentSummary.cs'
);
const MESSAGES_TS = path.resolve(__dirname, '../../types/messages.ts');
const SUB_AGENTS_API_TS = path.resolve(__dirname, '../../api/subAgentsApi.ts');

function read(file: string): string {
  return fs.readFileSync(file, 'utf-8');
}

/** Wire names the C# type publishes via `[JsonPropertyName("…")]`. */
function jsonPropertyNames(source: string): string[] {
  return [...source.matchAll(/\[JsonPropertyName\("([^"]+)"\)\]/g)].map((m) => m[1]);
}

/** Members of the named C# enum, which serialize verbatim (JsonStringEnumConverter, no policy). */
function enumMembers(source: string, enumName: string): string[] {
  const body = source.match(new RegExp(`enum\\s+${enumName}\\s*\\{([\\s\\S]*?)\\n\\}`));
  if (!body) return [];
  return [...body[1].matchAll(/^\s{4}(\w+),/gm)].map((m) => m[1]);
}

/** Serialized (camelCase) names of the C# record's `{ get; init; }` properties. */
function initOnlyPropertyNames(source: string): string[] {
  return [
    ...source.matchAll(/public\s+(?:required\s+)?[\w<>,?[\]. ]+?\s+(\w+)\s*\{\s*get;\s*init;\s*\}/g),
  ].map((m) => m[1][0].toLowerCase() + m[1].slice(1));
}

describe('isAgentMessage', () => {
  const agent: IMessage = {
    $type: MessageType.Agent,
    role: 'user',
  } as AgentMessage;

  it('recognizes an agent message even though its role is user', () => {
    expect(isAgentMessage(agent)).toBe(true);
    // The guard has to WIN against the role check, which is the ordering every consumer relies on.
    expect(agent.role).toBe('user');
  });

  it('does not claim the neighbouring out-of-band and text types', () => {
    expect(isAgentMessage({ $type: MessageType.Notify, role: 'user' })).toBe(false);
    expect(isAgentMessage({ $type: MessageType.Text, role: 'user' })).toBe(false);
    expect(isNotifyMessage(agent)).toBe(false);
    expect(isTextMessage(agent)).toBe(false);
  });

  it('narrows to the structured fields, not just the envelope text', () => {
    const msg: IMessage = {
      $type: MessageType.Agent,
      role: 'user',
      text: '<agent-message message-id="am-1" from="reviewer" …></agent-message>',
      message_id: 'am-1',
      agent_message_type: 'Question',
      from_agent_id: 'agent-2',
      from_name: 'reviewer',
    } as AgentMessage;

    expect(isAgentMessage(msg)).toBe(true);
    if (isAgentMessage(msg)) {
      // Reading these without a cast is the assertion: the UI never has to parse the envelope.
      expect(msg.from_name).toBe('reviewer');
      expect(msg.agent_message_type).toBe('Question');
    }
  });
});

describe('C#/TS parity — AgentMessage', () => {
  const cs = read(AGENT_MESSAGE_CS);
  const ts = read(MESSAGES_TS);

  it('mirrors every wire field the C# record publishes', () => {
    const names = jsonPropertyNames(cs);
    expect(names.length).toBeGreaterThan(5);
    for (const name of names) {
      expect(ts, `AgentMessage wire field '${name}' is missing from messages.ts`).toContain(name);
    }
  });

  it('mirrors every AgentMessageType member, verbatim and PascalCase', () => {
    const members = enumMembers(cs, 'AgentMessageType');
    expect(members).toContain('Question');
    expect(members).toContain('DelegateTask');
    for (const member of members) {
      expect(ts, `AgentMessageType '${member}' is missing from the TS union`).toContain(
        `'${member}'`
      );
    }
  });
});

describe('C#/TS parity — SubAgentSummary hierarchy metadata', () => {
  const cs = read(SUB_AGENT_SUMMARY_CS);
  const ts = read(SUB_AGENTS_API_TS);

  it('mirrors every property the C# summary serializes', () => {
    const names = initOnlyPropertyNames(cs);
    // Guard the scan itself: a regex that silently matched nothing would pass vacuously.
    expect(names).toContain('agentId');
    expect(names).toContain('collaborationId');
    for (const name of names) {
      expect(ts, `SubAgentSummary field '${name}' is missing from subAgentsApi.ts`).toContain(name);
    }
  });

  it('does not mistake the tab-kind constants for serialized properties', () => {
    // They are `public const string`, not `{ get; init; }` — publishing them would invent wire fields.
    expect(initOnlyPropertyNames(cs)).not.toContain('subAgentTabKind');
  });
});
