---
title: A decorator that always declares an optional store capability must fall back per member, not throw
date: 2026-10-23
type: bug
component: LmStreaming.Sample conversation-store decorators / LmMultiTurn optional store capabilities
tags: [decorator, optional-capability, conversation-store, fallback, notsupported]
applies_when:
  - A wrapper store (IndexedConversationStore, NonOwningConversationStore or a new one) implements an optional capability interface such as IConversationThreadIdLookupStore, IConversationRelationStore or IConversationUsageStore
  - Callers choose between a fast path and a fallback with `store is ICapability`
skip_when:
  - The wrapper only declares the capability when its inner store has it (for example a separate wrapper type per capability set)
source: "perf/reviewd-store-10x (#818): lead defect D2 and code-review-1 finding 2, 2026-10-23"
status: applied
revalidate_when: The optional capabilities become required members of IConversationStore, or the decorators stop declaring them unconditionally
---

## Problem
Callers detect an optional capability with `store is IConversationRelationStore` (or the usage and prefix-lookup
interfaces) and use a slow metadata path otherwise. The sample decorators always declare every capability, so
callers always take the fast path through them. Their members forwarded to the inner store and threw
`NotSupportedException` when the inner store lacked the capability. A File or InMemory store wrapped by a decorator
then failed hierarchy and usage calls (D2), and a custom store also failed its first sub-agent spawn (prefix lookup),
instead of falling back. The bug was made twice in one change, for two different interfaces.

## What didn't work
- A throwing `Capability<T>()` / `ThreadIdLookup` accessor: it turns an optional capability into a mandatory one
  for every wrapped store.
- Tests that wrapped only SQLite: SQLite implements every capability, so the throwing branch never ran.

## Solution
Each capability has one static capability-or-fallback helper next to its interface or its callers:
`ConversationThreadIdLookup.ListThreadIdsByPrefixAsync`, `ConversationForks.FindDirectForksAsync`,
`SubAgentProvenance.ListParentLinksAsync` and `ConversationUsageProjection.SaveAsync` / `LoadSnapshotAsync`.
Every decorator member calls the helper on its inner store. A test wraps a store that lacks the capability (a strict
mock of plain `IConversationStore`, or `InMemoryConversationStore`) in both decorators and asserts the fallback
result.

## Why it works
The helper puts the `is ICapability` check where the inner store is known, so declaring the capability on the wrapper
can no longer hide a missing capability on the store underneath. Any new decorator member and any new caller reuse the
same fallback, so it does not have to be rewritten for each one.
