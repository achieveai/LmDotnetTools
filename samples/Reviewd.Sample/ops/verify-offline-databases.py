"""Verify isolated working copies made by backup-offline-state.ps1, never its raw/source tree."""

import json
from pathlib import Path
import sqlite3
import sys


def verify(directory):
    results = []
    for path in sorted(Path(directory).glob("*.db")):
        # rw refuses a missing database instead of silently creating an empty, apparently valid one.
        with sqlite3.connect(path.resolve().as_uri() + "?mode=rw", uri=True) as database:
            rows = database.execute("PRAGMA integrity_check").fetchall()
            if rows != [("ok",)]:
                raise RuntimeError("IntegrityCheckFailed")
            results.append({
                "name": path.stem,
                "integrity": "ok",
                "schemaVersion": database.execute("PRAGMA user_version").fetchone()[0],
            })
        database.close()
    if not results:
        raise RuntimeError("NoDatabases")
    return results


if __name__ == "__main__":
    try:
        print(json.dumps(verify(sys.argv[1])))
    except Exception as error:
        # Do not emit database contents or SQLite error text from private state.
        print(type(error).__name__, file=sys.stderr)
        sys.exit(1)
