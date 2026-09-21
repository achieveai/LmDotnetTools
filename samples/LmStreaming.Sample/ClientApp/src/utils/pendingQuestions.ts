/**
 * Finding the questions a run is currently blocked on, so the client can dock them above the
 * chat input instead of burying them in the metadata pill.
 *
 * WHY THIS EXISTS: answering an `AskUserQuestion` is a capability the CLIENT exposes to the
 * server, not a rendering detail of the tool call that requested it. The tool call belongs in the
 * transcript (it is history); the thing the user has to *act on* belongs where they act — next to
 * the text box. Inside the pill it could be invisible for three compounding reasons: the pill body
 * is collapsed by default, the item list is a 150px scroll box, and that box auto-scrolls to the
 * bottom on every new item, pushing an earlier question out of view.
 *
 * Kept pure and mount-free (like `deriveToolPillState`, which it feeds) so both the main chat and
 * the sub-agent transcript run the SAME scan. Scoping a fix like this to one consumer of
 * `MessageList` has already shipped broken here once.
 */
import type { DisplayItem, ToolCall, ToolCallResultMessage } from '@/types';
import { resolveRenderer } from '@/utils/toolName';

/**
 * The `status` a question result carries when the SERVER settled the call early instead of the
 * user: something else arrived while the run was parked on the question, so the loop wrote this
 * placeholder, let the turn proceed, and promised to deliver the real answer later as a message.
 * Mirrors `EarlySettlePlaceholders.EarlySettleStatus` in LmMultiTurn.
 */
export const EARLY_SETTLE_STATUS = 'deferred_to_notification';

/** Prefix of the user-role text the server injects when a late answer is redirected (see below). */
export const USER_ANSWER_PREFIX = '<user-answer ';

/** True when `result` is the early-settle placeholder rather than an answer or a cancellation. */
export function isEarlySettledQuestionResult(result: string | null | undefined): boolean {
  if (!result || !result.includes(EARLY_SETTLE_STATUS)) return false;
  try {
    const parsed = JSON.parse(result) as { status?: unknown };
    return !!parsed && typeof parsed === 'object' && parsed.status === EARLY_SETTLE_STATUS;
  } catch {
    return false;
  }
}

/**
 * THE predicate for "this question is still waiting on the human" — every surface that decides
 * whether to show an answer form (inbox, dock, pill, activity row) goes through here.
 *
 * Two endings leave a question open. The ordinary one is the deferred-result protocol (#246):
 * `is_deferred: true` until the server republishes the SAME `tool_call_id` with the real result.
 * The other is an early settle: the server already wrote a non-deferred placeholder
 * (`status: deferred_to_notification`) so the run could continue, and the real answer never
 * overwrites it — it arrives as a separate `<user-answer …>` message instead. Such a question stays
 * answerable until that message exists (or this client submitted and was acked), which is what
 * `isAnswered` reports.
 */
export function isQuestionAwaitingAnswer(
  result: ToolCallResultMessage | null | undefined,
  isAnswered: (toolCallId: string) => boolean = () => false
): boolean {
  if (!result) return false;
  if (result.is_deferred) return true;
  if (!isEarlySettledQuestionResult(result.result)) return false;
  return !(result.tool_call_id && isAnswered(result.tool_call_id));
}

/** The pieces of a redirected answer the server injected as a user-role text message. */
export interface UserAnswerMessage {
  tool: string;
  toolCallId: string;
  /** The original request, as the server restated it (already human-readable for a question). */
  request: string;
  /** The raw answer payload the client submitted — for a question, `{"answers":[…]}`. */
  answer: string;
}

function unescapeAttribute(value: string): string {
  return value
    .replace(/&lt;/g, '<')
    .replace(/&gt;/g, '>')
    .replace(/&quot;/g, '"')
    .replace(/&apos;/g, "'")
    .replace(/&amp;/g, '&');
}

const REQUEST_OPEN = '<request>\n';
const REQUEST_TO_ANSWER = '\n</request>\n<answer>\n';
const ANSWER_CLOSE = '\n</answer>\n</user-answer>';

/**
 * Parse the text `MultiTurnAgentLoop.BuildEarlySettledAnswerMessage` injects:
 * `<user-answer tool="…" tool-call-id="…">` + request + answer, each body on its own lines. The
 * two bodies are verbatim payload, so they are cut at the envelope's own delimiters rather than
 * parsed as XML. Returns null for any ordinary message.
 */
export function parseUserAnswerMessage(text: string | null | undefined): UserAnswerMessage | null {
  if (!text || !text.startsWith(USER_ANSWER_PREFIX)) return null;
  const open = text.indexOf('>');
  if (open < 0) return null;
  const head = text.slice(USER_ANSWER_PREFIX.length, open);
  const id = /tool-call-id="([^"]*)"/.exec(head);
  if (!id || !id[1]) return null;
  const tool = /(?:^|\s)tool="([^"]*)"/.exec(head);
  const requestStart = text.indexOf(REQUEST_OPEN, open);
  const split = requestStart < 0 ? -1 : text.indexOf(REQUEST_TO_ANSWER, requestStart);
  const end = text.lastIndexOf(ANSWER_CLOSE);
  if (requestStart < 0 || split < 0 || end < split) return null;
  return {
    tool: unescapeAttribute(tool?.[1] ?? ''),
    toolCallId: unescapeAttribute(id[1]),
    request: text.slice(requestStart + REQUEST_OPEN.length, split),
    answer: text.slice(split + REQUEST_TO_ANSWER.length, end),
  };
}

/** The `tool_call_id` a redirected-answer message answers, or null for any other text. */
export function answeredQuestionIdFromText(text: string | null | undefined): string | null {
  return parseUserAnswerMessage(text)?.toolCallId ?? null;
}

/**
 * Every question a transcript already carries a redirected answer for, by `tool_call_id`. The
 * envelope is a user-role text on the wire, but the live stream wraps every incoming content
 * message as an assistant row (only the persisted copy keeps the role), so both text-bearing item
 * kinds are scanned — the prefix, not the bubble side, is what identifies the answer.
 */
export function collectAnsweredQuestionIds(displayItems: DisplayItem[]): Set<string> {
  const answered = new Set<string>();
  for (const item of displayItems) {
    if (item.type !== 'user-message' && item.type !== 'assistant-message') continue;
    const id = answeredQuestionIdFromText(item.content?.text);
    if (id) answered.add(id);
  }
  return answered;
}

/** A tool call whose deferred result is still outstanding — i.e. waiting on this user. */
export interface PendingQuestion {
  /** `tool_call_id`; non-empty by construction (a call without one cannot be answered). */
  id: string;
  toolCall: ToolCall;
  result: ToolCallResultMessage;
}

type ResultLookup = (toolCallId: string | null | undefined) => ToolCallResultMessage | null;

function isToolsCall(item: unknown): item is { tool_calls: ToolCall[] } {
  return !!item && Array.isArray((item as { tool_calls?: unknown }).tool_calls);
}

/**
 * Scan a transcript for question tool calls still awaiting an answer, in transcript order.
 *
 * "Awaiting an answer" is {@link isQuestionAwaitingAnswer}: the deferred-result protocol (#246),
 * where the server publishes a placeholder `ToolCallResultMessage` with `is_deferred: true` and
 * republishes the SAME `tool_call_id` with the real result once answered — or an early settle,
 * where the placeholder is final and the answer arrives later as a `<user-answer …>` message that
 * this scan also looks for. A call with no result at all is still streaming and has nothing to
 * answer. `isAnswered` lets the caller add what the transcript cannot show yet (its own acked
 * submission).
 *
 * Tool identity goes through {@link resolveRenderer} rather than a name comparison, so the
 * `sandbox-`-prefixed and differently-cased spellings of the same tool all resolve alike.
 */
export function findPendingQuestions(
  displayItems: DisplayItem[],
  getResult: ResultLookup,
  isAnswered: (toolCallId: string) => boolean = () => false
): PendingQuestion[] {
  const pending: PendingQuestion[] = [];
  const seen = new Set<string>();
  const answeredInTranscript = collectAnsweredQuestionIds(displayItems);
  const answered = (id: string) => answeredInTranscript.has(id) || isAnswered(id);

  for (const item of displayItems) {
    if (item.type !== 'pill') continue;
    for (const message of item.items) {
      if (!isToolsCall(message)) continue;
      for (const toolCall of message.tool_calls) {
        const id = toolCall.tool_call_id;
        // A duplicate id is the same logical call re-rendered (resume replays a pill), not a
        // second question — docking it twice would put two live forms on screen.
        if (!id || seen.has(id)) continue;
        if (resolveRenderer(toolCall.function_name).family !== 'question') continue;
        const result = getResult(id);
        if (!result || !isQuestionAwaitingAnswer(result, answered)) continue;
        seen.add(id);
        pending.push({ id, toolCall, result });
      }
    }
  }

  return pending;
}
