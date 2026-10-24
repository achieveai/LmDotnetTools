---
title: Clearing one SQLite pool leaves the test's own raw connections open, so Windows cannot delete the file
date: 2026-10-24
type: bug
component: LmMultiTurn SQLite test cleanup
tags: [sqlite, connection-pool, clearpool, windows, test-cleanup, flaky]
applies_when:
  - A test class replaces SqliteConnection.ClearAllPools() with a per-database clear such as SqliteConnectionFactory.ClearPool(path)
  - The same class opens its own SqliteConnection with a different connection string, such as "Data Source=<path>"
skip_when:
  - The class's raw connections set Pooling=False
source: "PR #824, Windows CI failure on 3ffdb82f (SqliteConversationStoreTests.MetadataWrites_KeepListingProjectionColumnsInSync)"
status: applied
revalidate_when: Microsoft.Data.Sqlite stops keying pools by the exact connection string, or the tests stop opening raw connections
---

## Problem
`SqliteConversationStoreTests.DisposeAsync` switched from `ClearAllPools()` to
`SqliteConnectionFactory.ClearPool(path)`, because clearing every pool disposed connections that other test
classes were using in parallel. Windows CI then failed in `DisposeAsync`:
`UnauthorizedAccessException` deleting `test_*.db-shm`. Linux never failed, because Linux deletes open files.

## What didn't work
- Trusting green runs. The change passed CI twice. It failed only when no other class happened to call
  `ClearAllPools()` between the test's raw read and its cleanup.

## Solution
`DisposeAsync` also clears the pool of the raw connection string:
`SqliteConnection.ClearPool(new SqliteConnection($"Data Source={path}"))`.

## Why it works
Microsoft.Data.Sqlite keys each pool by the exact connection string. `SqliteConnectionFactory.ClearPool` builds
the factory's string (`Mode`, `Cache`, `Pooling`). Four tests read the file through `"Data Source=<path>"`, a
second pool. Disposing those connections returned them to that pool, so the `.db` and `.db-shm` stayed open.
On Linux, check `/proc/self/fd` in `DisposeAsync` and run the class alone: without the extra clear, four tests
leave both files open; with it, none do.
