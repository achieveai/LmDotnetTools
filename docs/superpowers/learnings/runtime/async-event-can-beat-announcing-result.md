---
title: An async event can arrive before the synchronous result that announced its id
date: 2026-10-19
type: bug
component: LmAgentInfra sandbox webhook
tags: [webhook, race, ordering, correlation, background-task]
applies_when:
  - A webhook or other async event names an id that the app learns from a separate synchronous response (tool result, API reply)
  - The app correlates the two through state recorded when the response is processed
skip_when:
  - The producer documents and enforces that the response is delivered before the event is sent
source: "LmStreaming.Sample unified sandbox webhook migration, re-review F-001 (uncommitted, 2026-10-19)"
status: applied
revalidate_when: The gateway sends task_completed only after the starting tool response has been written, or adds a conversation id to the event
---

## Problem
The gateway's `task_completed` event for a primary agent carries no conversation. The app records the owning
conversation when the `Bash` tool result `Task started with ID: N` passes through `SandboxBackgroundTaskTracker`.
The gateway sends the event the moment the task ends (`webhook/dispatcher.rs`, `TaskWebhookSink.task_finished`
spawns the send), with no ordering against the tool response. So a fast task (`exit 1`) could reach
`SandboxTaskCompletionNotifier` before its owner was recorded. With two or more conversations on the session, the
notice was dropped.

## What didn't work
- Assuming the tool result always arrives first. Every live test used `sleep 3`, so the race never showed.
- Rating the risk from the app's code alone. The review lanes split MATERIAL/ADVISORY until someone read the
  producer's dispatch code.

## Solution
When no owner is recorded and the session has several conversations, the notifier polls the owner map for up
to `OwnerRecordWait` (2 s) before dropping (`WaitForOwnerAsync`). The regression test posts the event first, then
records the owner partway through the wait. Skipping the wait fails it.

## Why it works
Two channels from one producer are not ordered unless the producer orders them. Correlation state written by one
channel must tolerate being read by the other first: wait for a bounded time, or park the event until the record
arrives. A test only proves this when it drives the out-of-order case (event before record), not just the
natural order.
