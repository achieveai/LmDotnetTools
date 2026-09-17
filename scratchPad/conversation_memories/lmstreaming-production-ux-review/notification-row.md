# Notification activity-row research

## Current behavior

- Every notification is a purple rounded pill with a large emoji, bold generic kind, inline source tool, monospace label, and triangle. For `client-notification`, this makes “Notification” and “NotifyClient” louder than the actual message.
- A clickable-looking `div` handles both disclosure and descendant navigation. It lacks native keyboard behavior and disclosure state.
- Detail text is preserved in an expandable `<pre>`. Descendant questions navigate to an agent tab. Agent messages and completions carry an injected color cue. Compaction uses the same component with checkpoint and rollback semantics.

## Intended change

- Use a quiet neutral flat row, a 16px outline SVG selected by known notification kind, and proportional message text as the primary collapsed content.
- Move source tool name/call id into expanded metadata. A source-only row remains expandable so metadata is reachable.
- Render a native button for disclosure or navigation. Disclosure buttons expose `aria-expanded` and `aria-controls`; navigation buttons do not claim expansion. Rows with neither body, source metadata, nor navigation render a noninteractive header.
- This follows the [WAI-ARIA disclosure pattern](https://www.w3.org/WAI/ARIA/apg/patterns/disclosure/): a native button supplies Enter/Space activation and reports disclosure state through `aria-expanded`.
- Preserve detail text, known headings, descendant navigation, agent color cue, truncated marker, compaction checkpoint id, rollback badge, and compaction detail behavior.

## Test focus

- Generic NotifyClient row prioritizes its message and hides source metadata until expansion.
- Disclosure uses a native button with correct ARIA state and preserves raw detail plus source metadata.
- Descendant navigation remains a native button without fake disclosure attributes.
- No-body/no-source rows remain noninteractive.
