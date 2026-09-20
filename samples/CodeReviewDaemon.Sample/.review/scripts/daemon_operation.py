"""Transport to the configured daemon. All operation scope is resolved by its host."""
import os
import subprocess
import sys


def invoke(operation):
    executable = os.environ.get("REVIEW_DAEMON_EXECUTABLE")
    if not executable:
        print("REVIEW_DAEMON_EXECUTABLE must name the installed daemon executable or DLL.", file=sys.stderr)
        return 2
    command = [executable, "--workflow-operation", operation]
    if executable.lower().endswith(".dll"):
        command.insert(0, "dotnet")
    try:
        # Inherit stdin/stdout/stderr unchanged: one validated envelope and one JSON result.
        return subprocess.call(command)
    except OSError as error:
        print(f"Cannot start workflow operation: {error}", file=sys.stderr)
        return 1
