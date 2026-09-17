# Agent tab strip polish research

## Current behavior

- `ConversationTabs.vue` is presentational. Every entry is a native button with `role="tab"`, `aria-selected`, a stable `data-tab-id`, click selection, and a full `title` assembled by `tabTitle`.
- The title already preserves the complete agent name and adds workflow/status/failure context, so visual truncation does not remove the discoverable full name.
- Agent colors are assigned elsewhere and represented by a small dot. Workflow entries also carry a gear badge. These signals must remain.
- Horizontal overflow currently uses native `overflow-x: auto` with a thin scrollbar. Tabs never shrink and labels ellipsize within a 200px cap.
- Native buttons preserve ordinary Tab/Shift+Tab keyboard navigation. There is no custom arrow-key model in this component.
- Independent accessibility review found that the declared `tablist`/`tab` roles require the standard
  roving-tabindex model: one page tab stop, with Left/Right/Home/End moving and activating tabs.
- The current active treatment mixes the agent hue into the full tab background, changes all active text to that hue, adds a colored underline, and increases font weight. With many agents this produces a visually busy strip.
- No explicit active-tab `scrollIntoView` exists. A selected off-screen tab can therefore remain hidden when selection changes programmatically.

## Bounded change

- Keep the native button/ARIA/event contract, dots, workflow badge, and full contextual tooltip.
- Add minimal auto-activating Left/Right/Home/End navigation and make the active tab the sole `tabindex="0"` entry.
- Reduce tab padding and maximum width; use a neutral hover/active surface and reserve assigned color for the dot.
- Keep a thin scrollbar discoverable on strip hover/focus while making its idle chrome transparent;
  retain touch, wheel, keyboard-focus, and programmatic horizontal scrolling.
- On active-id or tab-list changes, scroll the selected native button into the nearest visible horizontal position after rendering.
- Add behavioral tests for the full-name tooltip and selected-tab scrolling. Avoid assertions that merely mirror CSS declarations.

## Risks checked

- `scrollIntoView` must use `block: nearest` so selecting a tab does not move the transcript vertically.
- Quiet scrollbar chrome must not disable `overflow-x: auto` or conceal the overflow affordance during interaction.
- Ellipsis requires the label to have `min-width: 0`; the dot and workflow badge remain fixed-size.
