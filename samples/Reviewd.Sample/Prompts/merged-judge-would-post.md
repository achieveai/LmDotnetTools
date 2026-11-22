## 12. A would-post review

`packet.ours.mode` is `"would-post"`: the review you judge posted nothing. It wrote the comments it would have
posted, and nobody on the PR saw them. This section overrides sections 1–11 where they conflict.

- **Ours** is `packet.ours.wouldPost[]`, ids `wp/1`, `wp/2`, …. The full text is in `would-post.json` (same ids), and
  `threads.md` opens with one line each. Each has a kind (`summary`, `inline` with a file and line, or `reply` to an
  existing thread id) and was checked against the review's own record. Wherever a step says "posted", read
  "would-post": build OURS from these comments, and match U against them. No comment on the PR is ours.
- **The live reviewer.** Comments carrying the `[Revobot]` marker are another reviewer's, posted live. They are in
  `packet.ours.liveReviewer[]` and are the `live-reviewer` class in `threads.md`. Use them as THEIRS from a bot:
  an issue they raise is in U, and our would-post review misses it if no `wp/` comment matches. Never credit them to
  ours, and never count them as human. Unmarked comments by the same identity are `operator` (human), as usual.
- **Timing.** Our review ran at `review.firstReviewAt`. Comments and fixes after it are evidence about the PR, not
  about what our review could have read.
- **Grades nobody could earn.** Nobody read our comments, so `packet.grading.notApplicable` lists what no review can
  show here: score `threadHelp` `N/A`; set `accuracy.fixRate.value` to `"N/A"` (num and den 0); leave
  `developerBehaviors` empty. Give the reason from the packet in each `why` or note. Grade `rightAmount` and
  `clearVerdict` from the would-post text, as if it had been posted.
- **Receipts.** `receipts.json` holds only the review's timeline and artifact fields. The reviewer's prompt and its
  answers are not inputs here; judge from the artifacts and the would-post comments.
- Do not try to learn which reviewer, model or configuration wrote this review. Grade what it would have posted.
