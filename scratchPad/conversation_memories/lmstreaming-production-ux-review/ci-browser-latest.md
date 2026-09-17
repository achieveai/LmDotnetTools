# Latest Browser E2E CI failure (`35189805838`)

## Evidence

- Only `ChatClientLayoutRegressionTests.Overflowing_conversation_scrolls_the_message_list_not_the_page_and_keeps_Clear_on_screen` failed; 101 passed and 5 skipped.
- Failure is at line 132 after pressing Tab from the open More menu: the test expects `chat-input-textarea` to be focused, but it is not.
- The blank root composer now renders an enabled project `WorkspaceSelector` before the textarea in DOM and keyboard order. The regression assertion/comment still describe the older composer, where the textarea was the first control after More.
- Workspace loading makes the stale assertion timing-sensitive: while the project picker is disabled, Tab skips it and reaches the textarea; after loading completes, Tab correctly focuses the picker. CI observed the latter.

## Root-cause hypothesis and minimal verification

- Root cause: the test asserts an obsolete focus target and can pass locally only when it races the initially disabled project picker.
- The meaningful contract is that Tab closes the roving More menu and enters the first enabled composer control. On a blank root chat, that is the project picker; a second Tab reaches the textarea.
- Reproduce the exact filtered scenario before editing. Then wait for the project picker to be enabled, assert focus moves to it after the first Tab, and assert the next Tab reaches the textarea. No production behavior should change.

## Verified fix
- Waits for enabled project picker before opening More; asserts menu dismissal, picker focus, then textarea focus on next Tab.
- Exact previously failing browser scenario passed locally: 1/1 in 12 seconds.
- Independent Sol review and Astra review passed. CSharpier and git diff checks passed.
- Regenerated the declaration sourceHash via inventory; only that manifest row changed.
- Reference: https://playwright.dev/dotnet/docs/test-assertions documents condition-based enabled/focus assertions. User request and CI evidence leave no implementation ambiguity.
