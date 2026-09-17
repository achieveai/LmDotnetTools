# Header overflow menu research

## Current ownership and rendering

- `ChatLayout.vue` owns both the full-width application header and the conversation content. `App.vue` only gates and mounts `ChatLayout`; there is no separate shell header component to change.
- `HeaderActionsMenu` is currently rendered in a second `chat-context-header` row inside `.chat-view`. That row contains no other controls, so moving the existing component removes the row completely.
- The application header's right group currently contains only the Work and agents panel toggle. It is the correct host for the More menu beside that toggle.
- Focus restoration after Marketplaces, Egress auth, Files, and Share closes is coupled to `headerActionsMenuRef`; moving the same component instance preserves it.
- Focus mode currently hides both the application-header right group and the secondary action row. Keeping the menu inside the existing `v-if="!focusMode"` right group preserves that behavior and prevents duplicates.
- `HeaderActionsMenu.vue` already implements menu-button semantics, enabled-item roving focus, Escape, Tab, outside-click closing, and exposed trigger focus. Only trigger presentation needs to change.

## Accessibility and visual direction

- WAI-ARIA's menu-button pattern calls for a native button with `aria-haspopup="menu"`, truthful `aria-expanded`, an associated menu, and Enter/Space opening focus on the first item. The existing implementation follows this and should remain unchanged: https://www.w3.org/WAI/ARIA/apg/patterns/menu-button/
- Use the accessible name `More`, matching the requested browser contract. Render a horizontal three-dot SVG with a 32–34px square control matching the existing panel buttons.
- Keep the popup right-aligned below the trigger. Its current absolute positioning remains suitable in the top-right header.

## Minimal change and tests

- Move the single `HeaderActionsMenu` node from `chat-context-header` into `.app-header-right`, immediately before the inspector launcher. Delete the now-empty secondary header markup and CSS.
- Restyle only the trigger to the icon control. Preserve existing test IDs and menu item IDs.
- Update existing `HeaderActionsMenu.test.ts` assertions for the accessible name/icon and `ChatLayout.test.ts` assertions for top-header placement and absence of the secondary row.
- Browser regression assumptions that Tab from More reaches the composer project picker and that More occupies a centered context row will need updating to the final DOM focus order and top-header geometry.
