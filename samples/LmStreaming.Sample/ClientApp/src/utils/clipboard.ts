/**
 * Copies plain text to the clipboard.
 *
 * `navigator.clipboard` exists only in a secure context (https or localhost). The sample is also opened
 * over plain http on a LAN address, where the async API is missing, so that case -- and a rejected write
 * (permission denied, document not focused) -- falls back to the legacy hidden-textarea `execCommand`.
 *
 * @throws when neither path copied the text.
 */
export async function copyTextToClipboard(text: string): Promise<void> {
  if (window.isSecureContext && navigator.clipboard?.writeText) {
    try {
      await navigator.clipboard.writeText(text);
      return;
    } catch {
      // Fall through to the legacy path.
    }
  }

  if (!copyWithExecCommand(text)) {
    throw new Error('Copy to clipboard failed');
  }
}

function copyWithExecCommand(text: string): boolean {
  const textarea = document.createElement('textarea');
  textarea.value = text;
  // Off-screen and read-only so selecting it neither scrolls the page nor opens a mobile keyboard.
  textarea.setAttribute('readonly', '');
  textarea.style.position = 'fixed';
  textarea.style.top = '-1000px';
  textarea.style.opacity = '0';
  document.body.appendChild(textarea);
  const previousFocus = document.activeElement as HTMLElement | null;
  try {
    textarea.focus();
    textarea.select();
    return typeof document.execCommand === 'function' && document.execCommand('copy');
  } catch {
    return false;
  } finally {
    textarea.remove();
    previousFocus?.focus?.();
  }
}
