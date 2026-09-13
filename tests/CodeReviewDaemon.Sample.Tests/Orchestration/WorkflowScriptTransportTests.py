"""Transport contract tests; never invoke a real daemon or provider."""
import importlib.util
import io
import os
from pathlib import Path
import unittest
from unittest.mock import patch

source = Path(__file__).resolve().parents[3] / "samples/CodeReviewDaemon.Sample/.review/scripts/daemon_operation.py"
spec = importlib.util.spec_from_file_location("daemon_operation", source)
transport = importlib.util.module_from_spec(spec)
spec.loader.exec_module(transport)


class WorkflowScriptTransportTests(unittest.TestCase):
    def test_missing_executable_fails_without_starting_process(self):
        with patch.dict(os.environ, {}, clear=True), patch.object(transport.subprocess, "call") as call, patch("sys.stderr", new_callable=io.StringIO) as errors:
            self.assertEqual(2, transport.invoke("collect-statistics"))
            call.assert_not_called()
            self.assertIn("REVIEW_DAEMON_EXECUTABLE", errors.getvalue())

    def test_dll_uses_dotnet_argument_list_and_preserves_exit_code(self):
        executable = "/installed path/daemon;$name.dll"
        with patch.dict(os.environ, {"REVIEW_DAEMON_EXECUTABLE": executable}), patch.object(transport.subprocess, "call", return_value=7) as call:
            self.assertEqual(7, transport.invoke("retain-artifacts"))
            call.assert_called_once_with(["dotnet", executable, "--workflow-operation", "retain-artifacts"])

    def test_native_executable_does_not_invoke_shell(self):
        with patch.dict(os.environ, {"REVIEW_DAEMON_EXECUTABLE": "/installed/daemon"}), patch.object(transport.subprocess, "call", return_value=0) as call:
            self.assertEqual(0, transport.invoke("prepare-review"))
            call.assert_called_once_with(["/installed/daemon", "--workflow-operation", "prepare-review"])

    def test_start_failure_is_nonzero_with_stderr_diagnostic(self):
        with patch.dict(os.environ, {"REVIEW_DAEMON_EXECUTABLE": "/missing/daemon"}), patch.object(transport.subprocess, "call", side_effect=OSError("not found")), patch("sys.stderr", new_callable=io.StringIO) as errors:
            self.assertEqual(1, transport.invoke("close-artifact-branch"))
            self.assertIn("not found", errors.getvalue())


if __name__ == "__main__":
    unittest.main()
