---
title: In mock mode, a compaction summary runs the first sub-agent's step 0, so that step must be a valid summary
date: 2026-10-23
type: knowledge
component: LmTestUtils TestMode / LmMultiTurn compaction
tags: [mock, instruction-chain, compaction, sub-agent, summary, manual-testing]
applies_when:
  - A test-mode (mock) conversation spawns a sub-agent and then compacts
  - A scripted walk or E2E test drives compaction through instruction chains
skip_when:
  - The conversation has no sub-agent before the compaction
  - A real model answers the summary call
source: "Loaded vs Available capability tiers, Manual Tester retrospective (reports/mt-slice2.md), 2026-10-23"
status: applied
revalidate_when: ConversationAnalyzer or InstructionChainParser stops taking the first chain in a user turn, or the summary prompt stops listing each roster child's task
---

## Problem
A mock conversation that spawned a sub-agent failed its compaction with `summary_call_failed`. The rows to
compact were fine. The mock answered the summary call with the child's first scripted step (a tool call),
not a summary.

## What didn't work
- Writing the trap down after the first hit. The same walk author hit it twice more (two failed compactions
  and one thrown-away thread each time).

## Solution
Give every child spawned before a compaction a step 0 that is a valid summary JSON text, followed by that
child's real first tool call if it needs one. Build this into the prompt generator, so no walk depends on
remembering it.

## Why it works
- The summary call appends one user turn. It lists the agent roster, and each entry carries the child's raw
  task. In test mode that task is the child's own `<|instruction_start|>` block. The roster comes before the
  rows.
- `ConversationAnalyzer.FindLatestInstructionChain` takes the newest user turn that holds a chain. That is the
  summary turn.
- `InstructionChainParser` then takes the first start tag in that text (`IndexOf`). That is the first roster
  child's chain, so its step 0 answers the summary call.
- A step 0 that is valid summary text satisfies the summary call and still lets the child run its own chain.
