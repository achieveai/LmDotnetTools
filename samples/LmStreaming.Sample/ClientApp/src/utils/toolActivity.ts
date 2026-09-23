/**
 * Human wording for ONE tool call: the activity-row one-liner ("Reading src/foo.ts", "Running npm
 * test") and the inline form the collapsed turn headline embeds ("Working: reading src/foo.ts").
 *
 * Extracted from ToolPill so the COLLAPSED headline (TurnActivity) names the running tool with the
 * exact same verbs the EXPANDED row shows — one tense/verb table in the codebase, not two.
 * Pure and mount-free (like `deriveToolPillState`, which feeds it); never throws.
 */
import type { ToolPillView } from './toolTypes';
import { normalizeToolName, resolveRenderer } from './toolName';

/**
 * The registry's collapsed summary for a call. Guarded because a `summarize` is only *documented*
 * never to throw — a bad entry must degrade to no detail, not blank the whole pill.
 */
export function summarizeToolCall(
  functionName: string | null | undefined,
  view: ToolPillView
): string {
  try {
    return resolveRenderer(functionName).summarize(view.parsedArgs, view.resultText, view);
  } catch {
    return '';
  }
}

/**
 * A described activity, plus whether its text opens with a VERB ("Reading a file") or with the
 * tool's own NAME ("My Custom Tool · foo: bar"). Only a verb-led phrase can be lower-cased into a
 * mid-sentence continuation — see {@link describeRunningTool}.
 */
export interface ToolActivityPhrase {
  text: string;
  leadsWithVerb: boolean;
}

/** First non-empty, trimmed string among `keys` in the call's parsed args, else ''. */
function stringArg(view: ToolPillView, ...keys: string[]): string {
  for (const key of keys) {
    const value = view.parsedArgs?.[key];
    if (typeof value === 'string' && value.trim()) return value.trim();
  }
  return '';
}

/** `my_tool` / `MyTool` → `My tool` (the fallback label for an unregistered tool). */
function humanizeToolName(name: string): string {
  const spaced = name.replace(/[_-]+/g, ' ').replace(/([a-z])([A-Z])/g, '$1 $2').trim();
  return spaced ? spaced.charAt(0).toUpperCase() + spaced.slice(1) : 'Tool activity';
}

/**
 * Tense-aware description of a tool call: present participle while it runs ("Reading …"), past
 * tense once it succeeded ("Read …"), failure wording on error. `summary` is the registry summary
 * for the same call (see {@link summarizeToolCall}) — passed in so a caller that already computed
 * it does not compute it twice.
 */
export function describeToolActivityPhrase(
  functionName: string | null | undefined,
  view: ToolPillView,
  summary: string
): ToolActivityPhrase {
  const succeeded = view.state === 'success';
  const failed = view.state === 'error';
  const detail = summary;
  const toolName = normalizeToolName(functionName);
  const verbLed = (text: string): ToolActivityPhrase => ({ text, leadsWithVerb: true });

  if (toolName === 'sendmessage') {
    const target = stringArg(view, 'target', 'agent_id');
    return verbLed(
      failed
        ? `Failed to send message${target ? ` to ${target}` : ''}`
        : `${succeeded ? 'Sent' : 'Sending'} message${target ? ` to ${target}` : ''}`
    );
  }
  if (toolName === 'agent') {
    const target = stringArg(view, 'subagent_type', 'name');
    return verbLed(
      failed
        ? `Failed to start${target ? ` ${target}` : ' agent'}`
        : `${succeeded ? 'Started' : 'Starting'}${target ? ` ${target}` : ' agent'}`
    );
  }
  if (toolName.includes('checkagent') || toolName === 'getagents') {
    return verbLed(`${failed ? 'Failed to check' : succeeded ? 'Checked' : 'Checking'} agent status`);
  }
  if (toolName === 'view_image') {
    return verbLed(
      failed ? 'Failed to view an image' : succeeded ? 'Viewed an image' : 'Viewing an image'
    );
  }

  switch (resolveRenderer(functionName).family) {
    case 'read': {
      const path = stringArg(view, 'file_path');
      return verbLed(`${failed ? 'Failed to read' : succeeded ? 'Read' : 'Reading'} ${path || 'a file'}`);
    }
    case 'write': {
      const path = stringArg(view, 'file_path') || 'file';
      return verbLed(`${failed ? 'Failed to write' : succeeded ? 'Wrote' : 'Writing'} ${path}`);
    }
    case 'edit': {
      const path = stringArg(view, 'file_path') || 'file';
      const stats = detail.includes('·') ? detail.slice(detail.indexOf('·') + 1).trim() : '';
      return verbLed(
        `${failed ? 'Failed to update' : succeeded ? 'Updated' : 'Updating'} ${path}${stats ? ` · ${stats}` : ''}`
      );
    }
    case 'shell':
      return verbLed(
        `${failed ? 'Command failed' : succeeded ? 'Ran' : 'Running'}${detail ? ` ${detail}` : ' command'}`
      );
    case 'grep':
    case 'glob':
      return verbLed(
        `${failed ? 'Search failed' : succeeded ? 'Searched for' : 'Searching for'}${detail ? ` ${detail}` : ''}`
      );
    case 'math':
      return verbLed(
        `${failed ? 'Calculation failed' : succeeded ? 'Calculated' : 'Calculating'}${detail ? ` ${detail}` : ''}`
      );
    case 'web':
      return verbLed(
        `${failed ? 'Web request failed' : succeeded ? 'Opened' : 'Opening'}${detail ? ` ${detail}` : ' web resource'}`
      );
    default: {
      // No verb for these: the phrase opens with the tool's own (humanized) name, so it keeps its
      // capitalization even when embedded mid-sentence.
      const label = humanizeToolName(functionName || toolName);
      return {
        text: `${failed ? `${label} failed` : label}${detail ? ` · ${detail}` : ''}`,
        leadsWithVerb: false,
      };
    }
  }
}

/** The activity-row one-liner for a tool call. */
export function describeToolActivity(
  functionName: string | null | undefined,
  view: ToolPillView,
  summary: string
): string {
  return describeToolActivityPhrase(functionName, view, summary).text;
}

/**
 * The same description rendered as a continuation of a sentence — "Working: reading src/foo.ts".
 * A verb-led phrase is lower-cased; a name-led one (unregistered tool) keeps its capitals.
 */
export function describeRunningTool(
  functionName: string | null | undefined,
  view: ToolPillView,
  summary: string
): string {
  const { text, leadsWithVerb } = describeToolActivityPhrase(functionName, view, summary);
  if (!text || !leadsWithVerb) return text;
  return text.charAt(0).toLowerCase() + text.slice(1);
}
