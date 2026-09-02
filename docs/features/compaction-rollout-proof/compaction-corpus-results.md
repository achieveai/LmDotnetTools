# Compaction corpus results

Evaluator `686.1`, generated 2026-09-02 23:18:36Z by `CompactionCorpusTests` (mock providers only; window 2400 tokens unless the scenario title says otherwise, reserve 0).

Latency is wall-clock on the machine that generated the report: reported, never asserted.

## (a) primary 40-turn tool-heavy

Fingerprint `3722036a2cf0ae6a`.

| Mode | Task | Calls (root+children) | Peak req tokens | Total prompt tokens | Cache hit | Checkpoints (root/child/shadow) | Cost µ$ (compaction) | Completeness | Latency ms (summary) | Pairs / loss / x-thread / raw |
|---|---|---|---|---|---|---|---|---|---|---|
| Off | FAILED (expected) | 9+0 | 2723 | 12339 | 78 % | 0/0/0 | 11724 (0) | Complete | 228 (0) | 0 / 0 / 0 / 0 |
| Shadow | FAILED (expected) | 9+0 | 2723 | 12339 | 78 % | 0/0/2 | 11724 (0) | Complete | 153 (0) | 0 / 0 / 0 / 0 |
| Compact | ok | 41+0 | 2050 | 67667 | 41 % | 20/0/0 | 146426 (6000) | Complete | 1826 (0) | 0 / 0 / 0 / 0 |

- Off: decisions none; reasons none; retention: instruction verbatim True, tasks on board True, agents match roster True, recovery zero True, index contiguous True (0 checkpoints); metadata keys 4, run-ledger entries 0, usage records 8.
  - 1 root request(s) exceeded the window at the provider
- Shadow: decisions no_action×5, shadow×2, skipped×1, warn×1; reasons below_threshold×1, hard×2; retention: instruction verbatim True, tasks on board True, agents match roster True, recovery zero True, index contiguous True (0 checkpoints); metadata keys 5, run-ledger entries 0, usage records 8.
  - 1 root request(s) exceeded the window at the provider
- Compact: decisions compact×20, no_action×5, skipped×15, warn×1; reasons below_threshold×15, hard×20; retention: instruction verbatim True, tasks on board True, agents match roster True, recovery zero True, index contiguous True (20 checkpoints); metadata keys 5, run-ledger entries 0, usage records 61.
  - checkpoint cp-0b7cf (Preemptive) boundary 10, 10 rows, 1033->146 tokens, agents [], tasks 0
  - checkpoint cp-275b6 (Preemptive) boundary 16, 16 rows, 1709->161 tokens, agents [], tasks 0
  - checkpoint cp-eda6e (Preemptive) boundary 23, 23 rows, 2385->177 tokens, agents [], tasks 0
  - checkpoint cp-d7c96 (Preemptive) boundary 30, 30 rows, 3061->192 tokens, agents [], tasks 0
  - checkpoint cp-55e3e (Preemptive) boundary 37, 37 rows, 3737->208 tokens, agents [], tasks 0
  - checkpoint cp-bd010 (Preemptive) boundary 44, 44 rows, 4413->223 tokens, agents [], tasks 0
  - checkpoint cp-8a6d2 (Preemptive) boundary 51, 51 rows, 5089->239 tokens, agents [], tasks 0
  - checkpoint cp-78dda (Preemptive) boundary 58, 58 rows, 5765->254 tokens, agents [], tasks 0
  - checkpoint cp-3d891 (Preemptive) boundary 65, 65 rows, 6441->270 tokens, agents [], tasks 0
  - checkpoint cp-44715 (Preemptive) boundary 72, 72 rows, 7117->285 tokens, agents [], tasks 0
  - checkpoint cp-af1ae (Preemptive) boundary 79, 79 rows, 7793->301 tokens, agents [], tasks 0
  - checkpoint cp-097be (Preemptive) boundary 86, 86 rows, 8469->316 tokens, agents [], tasks 0
  - checkpoint cp-afd71 (Preemptive) boundary 93, 93 rows, 9145->332 tokens, agents [], tasks 0
  - checkpoint cp-2d3ba (Preemptive) boundary 100, 100 rows, 9821->348 tokens, agents [], tasks 0
  - checkpoint cp-9812d (Preemptive) boundary 107, 107 rows, 10497->364 tokens, agents [], tasks 0
  - checkpoint cp-e0beb (Preemptive) boundary 110, 110 rows, 10835->380 tokens, agents [], tasks 0
  - checkpoint cp-3e954 (Preemptive) boundary 114, 114 rows, 11173->396 tokens, agents [], tasks 0
  - checkpoint cp-295a2 (Preemptive) boundary 117, 117 rows, 11511->412 tokens, agents [], tasks 0
  - checkpoint cp-01fb4 (Preemptive) boundary 121, 121 rows, 11849->428 tokens, agents [], tasks 0
  - checkpoint cp-77819 (Preemptive) boundary 125, 125 rows, 12187->444 tokens, agents [], tasks 0
  - 34 of 41 root requests carried a checkpoint envelope

## (b) mid-run correction + errored run

Fingerprint `4e0228ccd30eb270`.

| Mode | Task | Calls (root+children) | Peak req tokens | Total prompt tokens | Cache hit | Checkpoints (root/child/shadow) | Cost µ$ (compaction) | Completeness | Latency ms (summary) | Pairs / loss / x-thread / raw |
|---|---|---|---|---|---|---|---|---|---|---|
| Off | ok | 9+0 | 2102 | 9248 | 77 % | 0/0/0 | 9616 (0) | Complete | 243 (0) | 0 / 0 / 0 / 0 |
| Shadow | ok | 9+0 | 2123 | 9269 | 77 % | 0/0/1 | 9679 (0) | Complete | 130 (0) | 0 / 0 / 0 / 0 |
| Compact | ok | 9+0 | 1764 | 8364 | 64 % | 1/0/0 | 12027 (300) | Complete | 505 (0) | 0 / 0 / 0 / 0 |

- Off: decisions none; reasons none; retention: instruction verbatim True, tasks on board True, agents match roster True, recovery zero True, index contiguous True (0 checkpoints); metadata keys 5, run-ledger entries 0, usage records 8.
- Shadow: decisions no_action×7, shadow×1, warn×1; reasons hard×1; retention: instruction verbatim True, tasks on board True, agents match roster True, recovery zero True, index contiguous True (0 checkpoints); metadata keys 6, run-ledger entries 0, usage records 8.
- Compact: decisions compact×1, no_action×7, warn×1; reasons hard×1; retention: instruction verbatim True, tasks on board True, agents match roster True, recovery zero True, index contiguous True (1 checkpoints); metadata keys 6, run-ledger entries 0, usage records 9.
  - checkpoint cp-139e6 (Preemptive) boundary 13, 13 rows, 1072->155 tokens, agents [], tasks 2
  - 1 of 9 root requests carried a checkpoint envelope

## (c) primary spawning three sub-agents, one non-terminal at the cut

Fingerprint `3eb0f9d39baa4bfa`.

| Mode | Task | Calls (root+children) | Peak req tokens | Total prompt tokens | Cache hit | Checkpoints (root/child/shadow) | Cost µ$ (compaction) | Completeness | Latency ms (summary) | Pairs / loss / x-thread / raw |
|---|---|---|---|---|---|---|---|---|---|---|
| Off | ok | 11+3 | 2360 | 11374 | 79 % | 0/0/0 | 14287 (0) | Complete | 225 (0) | 0 / 0 / 0 / 0 |
| Shadow | ok | 11+3 | 2360 | 11374 | 79 % | 0/0/2 | 14287 (0) | Complete | 372 (0) | 0 / 0 / 0 / 0 |
| Compact | ok | 11+3 | 1928 | 9944 | 64 % | 1/0/0 | 17433 (300) | Complete | 708 (0) | 0 / 0 / 0 / 0 |

- Off: decisions none; reasons none; retention: instruction verbatim True, tasks on board True, agents match roster True, recovery zero True, index contiguous True (0 checkpoints); metadata keys 4, run-ledger entries 0, usage records 14.
- Shadow: decisions no_action×11, shadow×2, skipped×1; reasons below_threshold×1, hard×2; retention: instruction verbatim True, tasks on board True, agents match roster True, recovery zero True, index contiguous True (0 checkpoints); metadata keys 5, run-ledger entries 0, usage records 14.
- Compact: decisions compact×1, no_action×11, skipped×1, warn×1; reasons below_threshold×1, hard×1; retention: instruction verbatim True, tasks on board True, agents match roster True, recovery zero True, index contiguous True (1 checkpoints); metadata keys 5, run-ledger entries 0, usage records 15.
  - checkpoint cp-68cfe (Preemptive) boundary 16, 16 rows, 914->187 tokens, agents [56df034706ba-1be56d94:completed, 2eed84ee503d-1be56d94:running, 28b74fc8c02d-1be56d94:completed], tasks 0
  - 2 of 11 root requests carried a checkpoint envelope

## (d) sub-agent that compacts itself

Fingerprint `3dc83be360b121db`.

| Mode | Task | Calls (root+children) | Peak req tokens | Total prompt tokens | Cache hit | Checkpoints (root/child/shadow) | Cost µ$ (compaction) | Completeness | Latency ms (summary) | Pairs / loss / x-thread / raw |
|---|---|---|---|---|---|---|---|---|---|---|
| Off | ok | 2+7 | 81 | 99 | 18 % | 0/0/0 | 10712 (0) | Complete | 168 (0) | 0 / 0 / 0 / 0 |
| Shadow | ok | 2+7 | 81 | 99 | 18 % | 0/0/0 | 10712 (0) | Complete | 177 (0) | 0 / 0 / 0 / 0 |
| Compact | ok | 2+7 | 81 | 99 | 18 % | 0/1/0 | 13686 (0) | Complete | 126 (0) | 0 / 0 / 0 / 0 |

- Off: decisions none; reasons none; retention: instruction verbatim True, tasks on board True, agents match roster True, recovery zero True, index contiguous True (0 checkpoints); metadata keys 4, run-ledger entries 0, usage records 9.
- Shadow: decisions no_action×7, shadow×1, warn×1; reasons hard×1; retention: instruction verbatim True, tasks on board True, agents match roster True, recovery zero True, index contiguous True (0 checkpoints); metadata keys 4, run-ledger entries 0, usage records 9.
- Compact: decisions compact×1, no_action×7, warn×1; reasons hard×1; retention: instruction verbatim True, tasks on board True, agents match roster True, recovery zero True, index contiguous True (0 checkpoints); metadata keys 4, run-ledger entries 0, usage records 9.

## (e) workflow controller + two tasks

Fingerprint `9bc42f8f34c6e5ed`.

| Mode | Task | Calls (root+children) | Peak req tokens | Total prompt tokens | Cache hit | Checkpoints (root/child/shadow) | Cost µ$ (compaction) | Completeness | Latency ms (summary) | Pairs / loss / x-thread / raw |
|---|---|---|---|---|---|---|---|---|---|---|
| Off | ok | 9+0 | 2193 | 8361 | 74 % | 0/0/0 | 11129 (0) | Complete | 279 (0) | 0 / 0 / 0 / 0 |
| Shadow | ok | 9+0 | 2193 | 8361 | 74 % | 0/0/1 | 11129 (0) | Complete | 1984 (0) | 0 / 0 / 0 / 0 |
| Compact | ok | 9+0 | 1855 | 7672 | 56 % | 1/0/0 | 14371 (300) | Complete | 232 (0) | 0 / 0 / 0 / 0 |

- Off: decisions none; reasons none; retention: instruction verbatim True, tasks on board True, agents match roster True, recovery zero True, index contiguous True (0 checkpoints); metadata keys 4, run-ledger entries 0, usage records 9.
- Shadow: decisions no_action×7, shadow×1, skipped×1; reasons below_threshold×1, hard×1; retention: instruction verbatim True, tasks on board True, agents match roster True, recovery zero True, index contiguous True (0 checkpoints); metadata keys 5, run-ledger entries 0, usage records 9.
- Compact: decisions compact×1, no_action×7, skipped×1; reasons below_threshold×1, hard×1; retention: instruction verbatim True, tasks on board True, agents match roster True, recovery zero True, index contiguous True (1 checkpoints); metadata keys 5, run-ledger entries 0, usage records 10.
  - checkpoint cp-15db3 (Preemptive) boundary 13, 13 rows, 841->140 tokens, agents [], tasks 0
  - 1 of 9 root requests carried a checkpoint envelope

## (f) continuation after an interrupted stream

Fingerprint `6f8a05445eb87ac0`.

| Mode | Task | Calls (root+children) | Peak req tokens | Total prompt tokens | Cache hit | Checkpoints (root/child/shadow) | Cost µ$ (compaction) | Completeness | Latency ms (summary) | Pairs / loss / x-thread / raw |
|---|---|---|---|---|---|---|---|---|---|---|
| Off | ok | 9+0 | 2384 | 11654 | 80 % | 0/0/0 | 10807 (0) | Complete | 204 (0) | 0 / 0 / 0 / 0 |
| Shadow | ok | 9+0 | 2384 | 11654 | 80 % | 0/0/1 | 10807 (0) | Complete | 142 (0) | 0 / 0 / 0 / 0 |
| Compact | ok | 9+0 | 2046 | 10777 | 67 % | 1/0/0 | 14000 (300) | Complete | 103 (0) | 0 / 0 / 0 / 0 |

- Off: decisions none; reasons none; retention: instruction verbatim True, tasks on board True, agents match roster True, recovery zero True, index contiguous True (0 checkpoints); metadata keys 4, run-ledger entries 0, usage records 8.
- Shadow: decisions no_action×5, shadow×1, skipped×2, warn×1; reasons below_threshold×1, hard×1, unsafe_state×1; retention: instruction verbatim True, tasks on board True, agents match roster True, recovery zero True, index contiguous True (0 checkpoints); metadata keys 5, run-ledger entries 0, usage records 8.
- Compact: decisions compact×1, no_action×5, skipped×2, warn×1; reasons below_threshold×1, hard×1, unsafe_state×1; retention: instruction verbatim True, tasks on board True, agents match roster True, recovery zero True, index contiguous True (1 checkpoints); metadata keys 5, run-ledger entries 0, usage records 9.
  - checkpoint cp-a5a2f (Preemptive) boundary 10, 10 rows, 1032->143 tokens, agents [], tasks 0
  - 1 of 9 root requests carried a checkpoint envelope

## (g) deferred AskUserQuestion outstanding (must skip)

Fingerprint `304a1b30a92251d9`.

| Mode | Task | Calls (root+children) | Peak req tokens | Total prompt tokens | Cache hit | Checkpoints (root/child/shadow) | Cost µ$ (compaction) | Completeness | Latency ms (summary) | Pairs / loss / x-thread / raw |
|---|---|---|---|---|---|---|---|---|---|---|
| Off | ok | 8+0 | 2173 | 9397 | 77 % | 0/0/0 | 11086 (0) | Complete | 376 (0) | 0 / 0 / 0 / 0 |
| Shadow | ok | 8+0 | 2173 | 9397 | 77 % | 0/0/1 | 11086 (0) | Complete | 146 (0) | 0 / 0 / 0 / 0 |
| Compact | ok | 8+0 | 2046 | 8481 | 61 % | 1/0/0 | 14162 (300) | Complete | 261 (0) | 0 / 0 / 0 / 0 |

- Off: decisions none; reasons none; retention: instruction verbatim True, tasks on board True, agents match roster True, recovery zero True, index contiguous True (0 checkpoints); metadata keys 4, run-ledger entries 0, usage records 8.
- Shadow: decisions no_action×5, shadow×1, skipped×1, warn×1; reasons below_threshold×1, hard×1; retention: instruction verbatim True, tasks on board True, agents match roster True, recovery zero True, index contiguous True (0 checkpoints); metadata keys 5, run-ledger entries 0, usage records 8.
- Compact: decisions compact×1, no_action×5, skipped×1, warn×1; reasons below_threshold×1, hard×1; retention: instruction verbatim True, tasks on board True, agents match roster True, recovery zero True, index contiguous True (1 checkpoints); metadata keys 5, run-ledger entries 0, usage records 9.
  - checkpoint cp-5539d (Preemptive) boundary 10, 10 rows, 1032->104 tokens, agents [], tasks 0
  - 1 of 8 root requests carried a checkpoint envelope

## (h) parked Wait (must skip)

Fingerprint `09007ff66a6e630a`.

| Mode | Task | Calls (root+children) | Peak req tokens | Total prompt tokens | Cache hit | Checkpoints (root/child/shadow) | Cost µ$ (compaction) | Completeness | Latency ms (summary) | Pairs / loss / x-thread / raw |
|---|---|---|---|---|---|---|---|---|---|---|
| Off | ok | 8+0 | 2137 | 9347 | 77 % | 0/0/0 | 10974 (0) | Complete | 3160 (0) | 0 / 0 / 0 / 0 |
| Shadow | ok | 8+0 | 2137 | 9347 | 77 % | 0/0/1 | 10974 (0) | Complete | 3370 (0) | 0 / 0 / 0 / 0 |
| Compact | ok | 8+0 | 2044 | 8433 | 61 % | 1/0/0 | 14051 (300) | Complete | 3164 (0) | 0 / 0 / 0 / 0 |

- Off: decisions none; reasons none; retention: instruction verbatim True, tasks on board True, agents match roster True, recovery zero True, index contiguous True (0 checkpoints); metadata keys 4, run-ledger entries 0, usage records 8.
- Shadow: decisions no_action×5, shadow×1, skipped×1, warn×1; reasons below_threshold×1, hard×1; retention: instruction verbatim True, tasks on board True, agents match roster True, recovery zero True, index contiguous True (0 checkpoints); metadata keys 5, run-ledger entries 0, usage records 8.
- Compact: decisions compact×1, no_action×5, skipped×1, warn×1; reasons below_threshold×1, hard×1; retention: instruction verbatim True, tasks on board True, agents match roster True, recovery zero True, index contiguous True (1 checkpoints); metadata keys 5, run-ledger entries 0, usage records 9.
  - checkpoint cp-8b3bd (Preemptive) boundary 10, 10 rows, 1030->104 tokens, agents [], tasks 0
  - 1 of 8 root requests carried a checkpoint envelope

## (i) restart mid-conversation

Fingerprint `7be42b46cf75d133`.

| Mode | Task | Calls (root+children) | Peak req tokens | Total prompt tokens | Cache hit | Checkpoints (root/child/shadow) | Cost µ$ (compaction) | Completeness | Latency ms (summary) | Pairs / loss / x-thread / raw |
|---|---|---|---|---|---|---|---|---|---|---|
| Off | ok | 8+0 | 2089 | 9299 | 78 % | 0/0/0 | 10830 (0) | Complete | 225 (0) | 0 / 0 / 0 / 0 |
| Shadow | ok | 8+0 | 2089 | 9299 | 78 % | 0/0/1 | 10830 (0) | Complete | 143 (0) | 0 / 0 / 0 / 0 |
| Compact | ok | 8+0 | 2044 | 8385 | 62 % | 1/0/0 | 13907 (300) | Complete | 285 (0) | 0 / 0 / 0 / 0 |

- Off: decisions none; reasons none; retention: instruction verbatim True, tasks on board True, agents match roster True, recovery zero True, index contiguous True (0 checkpoints); metadata keys 4, run-ledger entries 0, usage records 8.
- Shadow: decisions no_action×5, shadow×1, skipped×1, warn×1; reasons below_threshold×1, hard×1; retention: instruction verbatim True, tasks on board True, agents match roster True, recovery zero True, index contiguous True (0 checkpoints); metadata keys 5, run-ledger entries 0, usage records 8.
- Compact: decisions compact×1, no_action×5, skipped×1, warn×1; reasons below_threshold×1, hard×1; retention: instruction verbatim True, tasks on board True, agents match roster True, recovery zero True, index contiguous True (1 checkpoints); metadata keys 5, run-ledger entries 0, usage records 9.
  - checkpoint cp-3babb (Preemptive) boundary 10, 10 rows, 1030->104 tokens, agents [], tasks 0
  - 1 of 8 root requests carried a checkpoint envelope

## (j) recall round trip

Fingerprint `88e9389c2a8883fe`.

| Mode | Task | Calls (root+children) | Peak req tokens | Total prompt tokens | Cache hit | Checkpoints (root/child/shadow) | Cost µ$ (compaction) | Completeness | Latency ms (summary) | Pairs / loss / x-thread / raw |
|---|---|---|---|---|---|---|---|---|---|---|
| Off | ok | 9+0 | 2451 | 12059 | 80 % | 0/0/0 | 12935 (0) | Complete | 265 (0) | 0 / 0 / 0 / 0 |
| Shadow | ok | 9+0 | 2439 | 12047 | 80 % | 0/0/2 | 12899 (0) | Complete | 267 (0) | 0 / 0 / 0 / 0 |
| Compact | ok | 9+0 | 2046 | 10354 | 65 % | 1/0/0 | 16012 (300) | Complete | 279 (0) | 0 / 0 / 0 / 0 |

- Off: decisions none; reasons none; retention: instruction verbatim True, tasks on board True, agents match roster True, recovery zero True, index contiguous True (0 checkpoints); metadata keys 4, run-ledger entries 0, usage records 9.
  - recall answered: {"error":"Unknown function: RecallConversation","available_functions":["Echo"]}
- Shadow: decisions no_action×5, shadow×2, skipped×1, warn×1; reasons below_threshold×1, hard×2; retention: instruction verbatim True, tasks on board True, agents match roster True, recovery zero True, index contiguous True (0 checkpoints); metadata keys 5, run-ledger entries 0, usage records 9.
  - recall answered: {"error":"nothing_compacted"}
- Compact: decisions compact×1, no_action×6, skipped×1, warn×1; reasons below_threshold×1, hard×1; retention: instruction verbatim True, tasks on board True, agents match roster True, recovery zero True, index contiguous True (1 checkpoints); metadata keys 5, run-ledger entries 0, usage records 10.
  - checkpoint cp-233f7 (Preemptive) boundary 10, 10 rows, 1032->143 tokens, agents [], tasks 0
  - 2 of 9 root requests carried a checkpoint envelope
  - recall returned the compacted instruction verbatim

## (k) legacy rows without Seq

Fingerprint `c436feaf557e67ae`.

| Mode | Task | Calls (root+children) | Peak req tokens | Total prompt tokens | Cache hit | Checkpoints (root/child/shadow) | Cost µ$ (compaction) | Completeness | Latency ms (summary) | Pairs / loss / x-thread / raw |
|---|---|---|---|---|---|---|---|---|---|---|
| Off | ok | 3+0 | 2274 | 5808 | 61 % | 0/0/0 | 8782 (0) | Complete | 739 (0) | 0 / 0 / 0 / 0 |
| Shadow | ok | 3+0 | 2274 | 5808 | 61 % | 0/0/1 | 8782 (0) | Complete | 804 (0) | 0 / 0 / 0 / 0 |
| Compact | ok | 3+0 | 1936 | 4977 | 32 % | 1/0/0 | 11816 (300) | Complete | 1011 (0) | 0 / 0 / 0 / 0 |

- Off: decisions none; reasons none; retention: instruction verbatim True, tasks on board True, agents match roster True, recovery zero True, index contiguous True (0 checkpoints); metadata keys 4, run-ledger entries 0, usage records 3.
- Shadow: decisions no_action×1, shadow×1, skipped×1; reasons below_threshold×1, hard×1; retention: instruction verbatim True, tasks on board True, agents match roster True, recovery zero True, index contiguous True (0 checkpoints); metadata keys 5, run-ledger entries 0, usage records 3.
- Compact: decisions compact×1, no_action×1, skipped×1; reasons below_threshold×1, hard×1; retention: instruction verbatim True, tasks on board True, agents match roster True, recovery zero True, index contiguous True (1 checkpoints); metadata keys 5, run-ledger entries 0, usage records 4.
  - checkpoint cp-97ad6 (Preemptive) boundary 4, 4 rows, 1264->421 tokens, agents [], tasks 0
  - 1 of 3 root requests carried a checkpoint envelope

## (l) unknown model (capacity unknown)

Fingerprint `1f94ff8bff47f9e0`.

| Mode | Task | Calls (root+children) | Peak req tokens | Total prompt tokens | Cache hit | Checkpoints (root/child/shadow) | Cost µ$ (compaction) | Completeness | Latency ms (summary) | Pairs / loss / x-thread / raw |
|---|---|---|---|---|---|---|---|---|---|---|
| Off | ok | 4+0 | 1033 | 2104 | 51 % | 0/0/0 | n/a (0) | Unavailable | 105 (0) | 0 / 0 / 0 / 0 |
| Shadow | ok | 4+0 | 1033 | 2104 | 51 % | 0/0/0 | n/a (0) | Unavailable | 189 (0) | 0 / 0 / 0 / 0 |
| Compact | ok | 4+0 | 1033 | 2104 | 51 % | 0/0/0 | n/a (0) | Unavailable | 309 (0) | 0 / 0 / 0 / 0 |

- Off: decisions none; reasons none; retention: instruction verbatim True, tasks on board True, agents match roster True, recovery zero True, index contiguous True (0 checkpoints); metadata keys 4, run-ledger entries 0, usage records 4.
- Shadow: decisions skipped×4; reasons capacity_unknown×4; retention: instruction verbatim True, tasks on board True, agents match roster True, recovery zero True, index contiguous True (0 checkpoints); metadata keys 4, run-ledger entries 0, usage records 4.
- Compact: decisions skipped×4; reasons capacity_unknown×4; retention: instruction verbatim True, tasks on board True, agents match roster True, recovery zero True, index contiguous True (0 checkpoints); metadata keys 4, run-ledger entries 0, usage records 4.

## (m) unpriced category (partial cost)

Fingerprint `6498734d29f85ebf`.

| Mode | Task | Calls (root+children) | Peak req tokens | Total prompt tokens | Cache hit | Checkpoints (root/child/shadow) | Cost µ$ (compaction) | Completeness | Latency ms (summary) | Pairs / loss / x-thread / raw |
|---|---|---|---|---|---|---|---|---|---|---|
| Off | ok | 8+0 | 2387 | 9632 | 75 % | 0/0/0 | 9561 (0) | Partial | 105 (0) | 0 / 0 / 0 / 0 |
| Shadow | ok | 8+0 | 2387 | 9632 | 75 % | 0/0/1 | 9561 (0) | Partial | 305 (0) | 0 / 0 / 0 / 0 |
| Compact | ok | 8+0 | 2049 | 8758 | 59 % | 1/0/0 | 13386 (300) | Partial | 243 (0) | 0 / 0 / 0 / 0 |

- Off: decisions none; reasons none; retention: instruction verbatim True, tasks on board True, agents match roster True, recovery zero True, index contiguous True (0 checkpoints); metadata keys 4, run-ledger entries 0, usage records 8.
- Shadow: decisions no_action×5, shadow×1, skipped×1, warn×1; reasons below_threshold×1, hard×1; retention: instruction verbatim True, tasks on board True, agents match roster True, recovery zero True, index contiguous True (0 checkpoints); metadata keys 5, run-ledger entries 0, usage records 8.
- Compact: decisions compact×1, no_action×5, skipped×1, warn×1; reasons below_threshold×1, hard×1; retention: instruction verbatim True, tasks on board True, agents match roster True, recovery zero True, index contiguous True (1 checkpoints); metadata keys 5, run-ledger entries 0, usage records 9.
  - checkpoint cp-55bdd (Preemptive) boundary 10, 10 rows, 1035->149 tokens, agents [], tasks 0
  - 1 of 8 root requests carried a checkpoint envelope

