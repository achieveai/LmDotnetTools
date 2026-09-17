# Sidebar single-line refinement research

## Existing structure

- Global New Chat and project Start both use the same custom 16px compose SVG. Replacing both paths with one standard 24px square-pen shape keeps their meaning consistent.
- Conversation rows currently stack title, preview, and timestamp, so each chat consumes three lines. Delete is a separate sibling button revealed on hover.
- The title is shortened in JavaScript before CSS truncation, which prevents the native tooltip from exposing the full title.

## Small implementation

- Render the complete title and let CSS ellipsis truncate it on one line.
- Remove the visible preview line. Put the full title and preview in the selection button's native `title`, preserving the hidden context without adding row height.
- Keep timestamp and delete as absolutely positioned right-side metadata. Reserve their width in the selection button so neither overlaps the title. Reveal both on row hover or `:focus-within`; keep Delete visible when it owns focus.
- Test semantic DOM and retained selection/deletion behavior. Avoid jsdom pixel or hover-layout assertions.

## Inventory correction

- `sourceHash` in `scripts/test-priorities.ndjson` is a declaration hash produced by `Get-TestInventory`, not a whole-file SHA-256. Never derive it with `Get-FileHash`; use the inventory output for changed C# declarations.

## Final review
- Icon reference: https://lucide.dev/icons/square-pen.
- Astra requested full title width at rest and stronger timestamp contrast; Sol applied both.
- Sol visual review passed resting and focused screenshots. Live preview rows remain 32.97px high in both states; unfocused timestamps have opacity 0.
- No ambiguity remains: the user explicitly requested single-line titles with hover times. Keyboard focus provides the same disclosure.
