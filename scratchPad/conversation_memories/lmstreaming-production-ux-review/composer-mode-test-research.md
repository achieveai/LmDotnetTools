# Composer mode placement test research

## Existing coverage to reuse

- `ChatInput.test.ts` already proves the root surface structure, the right-side `context-control` immediately before Send, and the optional project strip above the surface. Extend this contract with a left-side composer control slot instead of testing CSS pixels.
- `ChatLayout.test.ts` already covers mode event routing for started and messageless threads, disabled switching while streaming, provider placement, and provider exclusion from subagent composers. Adapt its placement fixture to prove one Mode control lives in the root composer, is absent from `.header-context`, and is absent from the subagent composer.
- `ModeSelector.test.ts` covers disabled behavior. Keep those tests; add presentation-specific semantics only if the product contract adds a prop or distinct test id.
- `ChatClientLayoutRegressionTests` already exercises keyboard focus around the old header Mode control. It is the smallest meaningful browser regression to update because moving Mode changes the real tab/focus sequence while retaining provider/send behavior. Avoid a new browser class.

## Small acceptance set

1. `ChatInput`: optional left composer control renders in the footer outside the right action group; provider remains immediately before Send. Omitting the slot keeps the shared/subagent composer unchanged.
2. `ChatLayout`: exactly one Mode selector exists in the root composer, none in the header, and switching still uses the existing local/backend handlers and disabled state.
3. `ChatLayout`: opening a subagent keeps the root Mode control unique and does not inject it into the child composer.
4. Existing ModeSelector disabled/edit tests stay green; add no styling snapshots.
5. Browser: update the existing layout regression's focus expectation to the new natural composer order, then verify provider and Send remain on the right and Mode is keyboard reachable.

## Risks

- A broad default slot can accidentally leak Mode into every `ChatInput`, including the subagent transcript.
- Keeping the old header render while adding the composer slot creates duplicate editable controls.
- Moving Mode can silently drop `disabled`, `select-mode`, create, or edit bindings even when it looks correct.
- DOM order must match the visual order for keyboard users: Mode on the left; provider then Send on the right.
