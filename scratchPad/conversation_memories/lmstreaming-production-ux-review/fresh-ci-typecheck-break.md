# Fresh CI type-check failure (`32701cf0`)

- Build workflow run `35189136602`, job `105097589950`, fails in `npm run type-check:test` before Vitest executes.
- Browser workflow run `35189136613`, job `105097589263`, fails at the identical SPA test type-check step. Browser restore, build, Playwright installation, and E2E execution are skipped.
- This is a tracked source regression after merging main's Workspace environment contract, not CI infrastructure.
- `Workspace.env` is now required. Three fixtures still omit it:
  - `src/__tests__/components/ChatLayout.test.ts:849`
  - `src/__tests__/components/ConversationSidebar.test.ts:8`
  - `src/__tests__/components/ConversationSidebar.test.ts:19`
- The minimal correction is to add the intended empty environment map (`env: {}`) to those three complete Workspace literals, then run `npm run type-check:test` and the affected component tests. No production change or browser-specific change is implicated.

Resolution: all three fixtures now include env: {}. Exact test type-check gate passes; affected component tests pass.
