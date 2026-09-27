# Shared helper provenance

This directory vendors **only** the existing SourcePool shared helper required by the sibling repo-setup skill. It is not a complete review-setup skill and has no SKILL.md entry point. Do not install it as a replacement for a complete existing review-setup distribution.

`scripts/review_common.py` was originally imported byte-for-byte on 2026-09-20 from the operator-approved frozen helper already installed and verified in NOVA_reviews. The original upstream SHA256 was:

```text
07965430bd1be7d88b11d879bb7651e39dcb10d6fb42a172c6eab1cbf9d3e67f
```

The current vendored copy adds explicit `--relative-paths` at its three `git worktree add` sites, a shared read-only link validator, and lifecycle-locked normalization of preflighted root/pool worktree links. The sibling repo-setup script also requests relative links for root checkouts. This requires Git 2.48 or newer and enables Git's native `relativeWorktrees` repository extension; older Git or embedded Git clients may not support the resulting repositories. No global Git configuration is changed.

SourcePool store identity, lifecycle/acquisition/source lock ordering, safe Git subprocess behavior and existing review-state formats are unchanged. The sibling script deliberately does not call the slot reset/clean helpers or baseline publication APIs. Root setup apply now preflights all owned registrations and uses native `git worktree repair --relative-paths` for correctly resolving absolute forward/back links before network work. Already-relative stores are not repaired. Foreign, broken, symlinked or locked metadata is refused before repair; an absolute `commondir` is also refused because native repair does not normalize it. HEAD/index identity is checked across repair, and root/daemon-slot acceptance requires all three links to be relative. Read-only planning never repairs.

The helper uses Python standard-library dependencies (including Linux `fcntl`). Copy this sibling helper alongside repo-setup when packaging for another repository or GB plugins. A future full review-setup import should adopt this same canonical copy rather than add another fork. Operator installations must compare hashes and preserve differing existing assets for review instead of overwriting them blindly.
