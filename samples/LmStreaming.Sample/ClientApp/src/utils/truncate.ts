/**
 * Truncates text to the specified max length, adding ellipsis if needed.
 */
export function truncateText(text: string, maxLen: number): string {
  if (!text || text.length <= maxLen) {
    return text || '';
  }
  return text.slice(0, maxLen).trim() + '...';
}

/**
 * Formats a tool call preview with truncated function args.
 * Output: "func_name(arg1: '...', arg2: '...')"
 */
export function formatToolCallPreview(
  functionName: string,
  argsJson: string | null | undefined,
  maxArgLen: number = 15
): string {
  if (!functionName) {
    return 'Unknown function';
  }

  if (!argsJson) {
    return `${functionName}()`;
  }

  try {
    const args = JSON.parse(argsJson);
    const entries = Object.entries(args);

    if (entries.length === 0) {
      return `${functionName}()`;
    }

    const formattedArgs = entries
      .slice(0, 2)
      .map(([key, value]) => {
        const strValue = typeof value === 'string' ? value : JSON.stringify(value);
        const truncated = truncateText(strValue, maxArgLen);
        return `${key}: '${truncated}'`;
      })
      .join(', ');

    const suffix = entries.length > 2 ? ', ...' : '';
    return `${functionName}(${formattedArgs}${suffix})`;
  } catch {
    // If parsing fails, just show function name
    return `${functionName}(...)`;
  }
}

/**
 * Formats a tool result preview.
 * Output: "func_name: result_preview"
 */
export function formatToolResultPreview(
  functionName: string,
  result: string | null | undefined,
  maxResultLen: number = 30
): string {
  if (!functionName) {
    return truncateText(result || '', maxResultLen);
  }

  if (!result) {
    return `${functionName}: (no result)`;
  }

  return `${functionName}: ${truncateText(result, maxResultLen)}`;
}

/**
 * Formats a combined tool call + result preview.
 * Output: "func_name(args...) = result"
 */
export function formatToolCallWithResult(
  functionName: string,
  argsJson: string | null | undefined,
  result: string | null | undefined,
  maxLen: number = 40
): string {
  const callPreview = formatToolCallPreview(functionName, argsJson, 10);

  if (!result) {
    return callPreview;
  }

  // Calculate remaining space for result
  const remainingLen = Math.max(10, maxLen - callPreview.length - 3); // 3 for " = "
  const resultPreview = truncateText(result, remainingLen);

  return `${callPreview} = ${resultPreview}`;
}
