## Summary

This document outlines a query-level architecture for a standalone C# application backed by SQLite, using **Microsoft.Data.Sqlite** as the ADO.NET provider, the **sqlite-vec** extension (`vec0`) for approximate nearest-neighbor (ANN) searches over embeddings, and **FTS5** for full-text indexing. Graph structures (nodes and edges) are modeled in relational tables and traversed via **recursive CTEs**. All interactions occur through raw SQL and prepared commands—no Entity Framework—ensuring maximum control and transparency over queries.

---

## Architecture Overview

### Components

* **Connection Manager**: Manages `SqliteConnection` lifecycles, enabling extensions and opening connections reliably ([Microsoft Learn][1]).
* **Extension Loader**: Immediately after opening a connection, calls `EnableExtensions(true)` and `LoadExtension(...)` to register both **sqlite-vec** and the FTS5 module ([Microsoft Learn][2], [Microsoft Learn][3]).
* **Data Access Layer**: Exposes methods that execute parameterized SQL for graph traversals, vector K-NN, and text searches, all via `SqliteCommand` objects ([Microsoft Learn][4]).
* **Query Executor**: Runs the SQL against the database, maps results to C# models, and handles exceptions and transactions.

### Extension Loading Sequence

```csharp
var conn = new SqliteConnection("Data Source=app.db");
conn.Open();
conn.EnableExtensions(true);
conn.LoadExtension("vec0");        // sqlite-vec virtual table
conn.LoadExtension("mod_spatialite"); // example; FTS5 typically built-in
```

> **Note:** On Windows the module is `vec0.dll`; on Linux/macOS use `vec0.so`. ([SQLite][5])

---

## Data Modeling

### 1. Graph Data

* **Tables**

  ```sql
  CREATE TABLE Nodes (
    Id   INTEGER PRIMARY KEY,
    Data TEXT
  );
  CREATE TABLE Edges (
    FromNode INTEGER NOT NULL,
    ToNode   INTEGER NOT NULL,
    Weight   REAL    DEFAULT 1.0,
    FOREIGN KEY(FromNode) REFERENCES Nodes(Id),
    FOREIGN KEY(ToNode)   REFERENCES Nodes(Id)
  );
  ```

  ([SQLite][6])

* **Traversal via Recursive CTE**

  ```sql
  WITH RECURSIVE Traverse(id, depth, path) AS (
    SELECT $startId, 0, printf('%d', $startId)
    UNION ALL
    SELECT e.ToNode, t.depth + 1, t.path || '->' || e.ToNode
    FROM Edges e
    JOIN Traverse t ON e.FromNode = t.id
    WHERE t.depth < $maxDepth
  )
  SELECT * FROM Traverse;
  ```

  ([SQLite][7])

### 2. Embeddings (ANN)

* **Storage**

  ```sql
  CREATE TABLE Items (
    Id        INTEGER PRIMARY KEY,
    Metadata  TEXT
  );
  CREATE VIRTUAL TABLE ItemEmbeddings 
    USING vec0(id=Id, embedding BLOB);
  ```

  **sqlite-vec** embeds vector storage and KNN search directly into SQLite ([GitHub][8], [Medium][9]).

* **K-NN Query**

  ```sql
  SELECT Id, distance
  FROM ItemEmbeddings
  ORDER BY embedding <-> $queryVector
  LIMIT $k;
  ```

  Here `<->` is the cosine-distance operator provided by `vec0`. ([DEV Community][10])

### 3. Full-Text Search

* **FTS5 Table**

  ```sql
  CREATE VIRTUAL TABLE Documents
  USING fts5(Title, Body);
  ```

  **FTS5** provides tokenization, ranking, and phrase searching within SQLite ([SQLite][11]).

* **Text Query**

  ```sql
  SELECT rowid, snippet(Documents)
  FROM Documents
  WHERE Documents MATCH $searchQuery;
  ```

  ([SQLite][12])

---

## Data Access Layer

### Connection and Extension Management

```csharp
public class SqliteManager
{
    private readonly string _connectionString;

    public SqliteManager(string dbPath) =>
        _connectionString = $"Data Source={dbPath}";

    public SqliteConnection CreateConnection()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        conn.EnableExtensions(true);
        conn.LoadExtension("vec0");       // sqlite-vec
        // FTS5 often built-in; no explicit load needed
        return conn;
    }
}
```

This pattern ensures every connection is ready for graph, vector, and text features ([Microsoft Learn][1], [Microsoft Learn][3]).

### Executing Queries

* Use `SqliteCommand` with parameter binding (`$param`) to prevent injection and maximize performance ([Microsoft Learn][4]).

```csharp
using var cmd = connection.CreateCommand();
cmd.CommandText = "SELECT * FROM Nodes WHERE Id = $id";
cmd.Parameters.AddWithValue("$id", nodeId);
using var rdr = cmd.ExecuteReader();
// Map rdr to C# model...
```

---

## Query Patterns

### Graph Search Example

```csharp
cmd.CommandText = @"
  WITH RECURSIVE Traverse(id, depth) AS (
    SELECT $start, 0
    UNION ALL
    SELECT e.ToNode, t.depth+1
    FROM Edges e
    JOIN Traverse t ON e.FromNode = t.id
    WHERE t.depth < $maxDepth
  )
  SELECT id FROM Traverse;
";
```

Bind `$start` and `$maxDepth` before execution ([SQLite][6], [SQLite][7]).

### ANN Search Example

```csharp
cmd.CommandText = @"
  SELECT Id 
  FROM ItemEmbeddings
  ORDER BY embedding <-> $vector
  LIMIT $k;
";
cmd.Parameters.AddWithValue("$vector", queryBlob);
cmd.Parameters.AddWithValue("$k", 10);
```

The `<->` operator and `LIMIT` yield the top-k nearest items ([Medium][9], [DEV Community][10]).

### Full-Text Search Example

```csharp
cmd.CommandText = @"
  SELECT rowid, highlight(Documents)
  FROM Documents
  WHERE Documents MATCH $term;
";
cmd.Parameters.AddWithValue("$term", "brown fox");
```

The `MATCH` operator leverages the FTS5 index for sub-second lookups even on large corpora ([SQLite][11], [SQLite][12]).

---

## Performance and Best Practices

* **Indexing**: Add traditional indexes on `Edges(FromNode)`, `Edges(ToNode)` for faster joins ([SQLite][6]).
* **Batching**: Wrap bulk inserts in a single transaction to minimize journal overhead.
* **Memory**: Consider `PRAGMA cache_size` to tune SQLite’s page cache for heavy graph traversals ([SQLite][5]).
* **Virtual Table Integrity**: Use `PRAGMA integrity_check` periodically to validate FTS5 and vec0 tables ([SQLite][13]).

---

## Deployment and Packaging

* **Bundling Extensions**: Include `vec0.dll`/`.so` alongside your application binaries and ensure the working directory or `PATH`/`LD_LIBRARY_PATH` can locate them ([Microsoft Learn][2]).
* **Cross-Platform Builds**: Compile `sqlite-vec` once per platform or use the WASM build for browser-based use cases ([GitHub][8]).
* **Version Control**: Keep your schema migrations scripts under version control, and apply them at startup via ADO.NET before production use.

---

With this design, you’ll have a lightweight, high-performance C# application that fully leverages SQLite’s modern extensions—graph traversals, ANN for embeddings, and robust full-text search—entirely at the SQL query level.

[1]: https://learn.microsoft.com/en-us/dotnet/api/microsoft.data.sqlite.sqliteconnection?view=msdata-sqlite-8.0.0&utm_source=chatgpt.com "SqliteConnection Class (Microsoft.Data.Sqlite)"
[2]: https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/extensions?utm_source=chatgpt.com "Extensions - Microsoft.Data.Sqlite"
[3]: https://learn.microsoft.com/en-us/dotnet/api/microsoft.data.sqlite.sqliteconnection.loadextension?view=msdata-sqlite-9.0.0&utm_source=chatgpt.com "SqliteConnection.LoadExtension(String, String) Method"
[4]: https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/?utm_source=chatgpt.com "Microsoft.Data.Sqlite overview"
[5]: https://www.sqlite.org/loadext.html?utm_source=chatgpt.com "Run-Time Loadable Extensions - SQLite"
[6]: https://sqlite.org/lang_with.html?utm_source=chatgpt.com "3. Recursive Common Table Expressions - SQLite"
[7]: https://sqlite.org/forum/info/3b309a9765636b79?utm_source=chatgpt.com "Breadth-first graph traversal - SQLite User Forum"
[8]: https://github.com/asg017/sqlite-vec?utm_source=chatgpt.com "asg017/sqlite-vec: A vector search SQLite extension that ... - GitHub"
[9]: https://medium.com/%40stephenc211/how-sqlite-vec-works-for-storing-and-querying-vector-embeddings-165adeeeceea?utm_source=chatgpt.com "How sqlite-vec Works for Storing and Querying Vector Embeddings"
[10]: https://dev.to/stephenc222/how-to-use-sqlite-vec-to-store-and-query-vector-embeddings-58mf?utm_source=chatgpt.com "How to use sqlite-vec to store and query vector embeddings"
[11]: https://www.sqlite.org/fts5.html?utm_source=chatgpt.com "SQLite FTS5 Extension"
[12]: https://sqlite.org/search?q=fts5&utm_source=chatgpt.com "Search SQLite Documentation"
[13]: https://www.sqlite.org/vtab.html?utm_source=chatgpt.com "The Virtual Table Mechanism Of SQLite"
