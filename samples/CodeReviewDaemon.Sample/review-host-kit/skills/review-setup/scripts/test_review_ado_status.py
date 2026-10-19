"""Regression for completed PRs reaching a missing merge-ref fetch."""
import unittest
from unittest.mock import patch

import review_ado
from review_common import ResolvedPullRequest, ReviewError


class PullRequestStatusTests(unittest.TestCase):
    def provider(self):
        provider = object.__new__(review_ado.AdoProvider)
        provider._transport = object()
        return provider

    def test_terminal_pr_is_skippable_before_iteration_or_git_reads(self):
        for status in ("completed", "abandoned"):
            with self.subTest(status=status):
                with patch.object(review_ado, "fetch_pr", return_value=(5720578, {"status": status, "repository": {"name": "NovaClient"}})), patch.object(review_ado, "resolve_pr") as resolve:
                    with self.assertRaises(ReviewError) as caught:
                        self.provider().resolve(5720578, repo_hint="NovaClient")
                    self.assertEqual(4, caught.exception.code)
                    self.assertIn("PR_NOT_ACTIVE", str(caught.exception))
                    self.assertIn(status, str(caught.exception))
                    resolve.assert_not_called()

    def test_active_and_unknown_statuses_are_not_terminal(self):
        snapshot = {"pr_number": 5720578, "repository": {"name": "NovaClient", "url": "https://dev.azure.com/org/project/_git/NovaClient"}, "source_ref": "refs/heads/source", "target_ref": "refs/heads/main", "iteration_id": 1}
        for status in ("active", "unknown", None):
            with self.subTest(status=status):
                with patch.object(review_ado, "fetch_pr", return_value=(5720578, {"status": status})), patch.object(review_ado, "resolve_pr", return_value=snapshot), patch.object(review_ado, "_merge_sha_from_payload", return_value="a" * 40):
                    self.assertIsInstance(self.provider().resolve(5720578), ResolvedPullRequest)

    def test_wrong_repository_is_not_a_skippable_terminal_result(self):
        with patch.object(review_ado, "fetch_pr", return_value=(5720578, {"status": "completed", "repository": {"name": "Astra"}})):
            with self.assertRaises(ReviewError) as caught:
                self.provider().resolve(5720578, repo_hint="NovaClient")
            self.assertEqual(1, caught.exception.code)

    def test_auth_failure_keeps_normal_error_code(self):
        with patch.object(review_ado, "fetch_pr", side_effect=ReviewError("Authentication required")):
            with self.assertRaises(ReviewError) as caught:
                self.provider().resolve(5720578)
            self.assertEqual(1, caught.exception.code)


if __name__ == "__main__":
    unittest.main()
