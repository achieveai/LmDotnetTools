"""GitHub source coordinates and PR state mapping for review-setup."""
import re
import unittest
from unittest.mock import patch

import review_github
from review_common import ReviewError, parse_source_url

HEAD, BASE, MERGE = "a" * 40, "b" * 40, "c" * 40


def pr(**overrides):
    payload = {"number": 7, "state": "open", "draft": False, "merged_at": None, "mergeable": True,
               "created_at": "2026-10-01T00:00:00Z", "updated_at": "2026-10-02T00:00:00Z",
               "merge_commit_sha": MERGE, "title": "t", "body": "Fixes #3", "html_url": "u",
               "user": {"login": "dev"}, "head": {"sha": HEAD, "ref": "feat/x"},
               "base": {"sha": BASE, "ref": "main"}}
    payload.update(overrides)
    return payload


class SourceUrlTests(unittest.TestCase):
    def test_github_url_parses_to_owner_and_repository(self):
        parsed = parse_source_url("https://github.com/achieveai/LmDotnetTools.git")
        self.assertEqual((parsed["provider"], parsed["owner"], parsed["repository"], parsed["project"]),
                         ("github", "achieveai", "LmDotnetTools", None))

    def test_ado_url_still_parses_as_ado(self):
        parsed = parse_source_url("https://dev.azure.com/org/Proj/_git/Repo")
        self.assertEqual((parsed["provider"], parsed["repository"]), ("ado", "Repo"))

    def test_unsafe_github_urls_are_refused(self):
        for url in ("http://github.com/a/b", "https://user:pw@github.com/a/b", "https://github.com/a/b/c",
                    "https://github.com/a/b?x=1", "https://github.com:8443/a/b", "https://gitlab.com/a/b"):
            with self.subTest(url=url), self.assertRaises(ReviewError):
                parse_source_url(url)


class LiveStateTests(unittest.TestCase):
    def test_open_mergeable_pr_reports_active_with_merge(self):
        state = review_github.live_state(pr())
        self.assertEqual((state["status"], state["isDraft"], state["headSha"], state["mergeSha"]),
                         ("active", False, HEAD, MERGE))

    def test_conflicted_pr_has_no_merge_sha(self):
        self.assertIsNone(review_github.live_state(pr(mergeable=False))["mergeSha"])

    def test_closed_prs_map_to_completed_or_abandoned(self):
        self.assertEqual(review_github.live_state(pr(state="closed", merged_at="x"))["status"], "completed")
        self.assertEqual(review_github.live_state(pr(state="closed"))["status"], "abandoned")


class ResolveTests(unittest.TestCase):
    def provider(self, payload):
        provider = review_github.GitHubProvider("achieveai", "LmDotnetTools")
        patcher = patch.object(provider.client, "request", return_value=payload)
        patcher.start()
        self.addCleanup(patcher.stop)
        return provider

    def test_resolve_uses_the_github_test_merge_ref(self):
        resolved = self.provider(pr()).resolve(7, repo_hint="lmdotnettools")
        self.assertEqual((resolved.provider, resolved.merge_ref, resolved.merge_sha, resolved.remote_url),
                         ("github", "refs/pull/7/merge", MERGE, "https://github.com/achieveai/LmDotnetTools.git"))

    def test_closed_pr_is_not_active_with_skip_code(self):
        with self.assertRaises(review_github.PullRequestNotActive) as caught:
            self.provider(pr(state="closed")).resolve(7)
        self.assertEqual(caught.exception.code, 4)

    def test_mismatched_repository_hint_is_refused(self):
        with self.assertRaises(ReviewError):
            self.provider(pr()).resolve(7, repo_hint="Other")


class FakeGraphQL:
    """Answers commit-to-PR batches; ``missing`` commits come back null, ``fail`` batches raise."""

    owner, repository = "achieveai", "LmDotnetTools"

    def __init__(self, missing=(), fail=False):
        self.queries, self.missing, self.fail = [], set(missing), fail

    def graphql(self, query, variables):
        self.queries.append(query)
        if self.fail:
            raise ReviewError("down")
        oids = re.findall(r'(c\d+): object\(oid: "([0-9a-f]{40})"\)', query)
        return {"repository": {alias: None if oid in self.missing else {"associatedPullRequests": {"nodes": [
            {"number": 5, "title": "Origin", "body": "b", "state": "MERGED", "closedAt": "c", "url": "u",
             "author": {"login": "dev"}}]}} for alias, oid in oids}}


class OriginPrTests(unittest.TestCase):
    commits = [f"{index:040x}" for index in range(250)]

    def test_a_large_pr_is_queried_in_batches_and_stays_complete(self):
        client = FakeGraphQL()
        result = review_github._origin_prs(client, self.commits + self.commits[:3])
        self.assertTrue(result["complete"])
        self.assertEqual(len(client.queries), 3)
        self.assertTrue(all(q.lstrip().startswith("query") for q in client.queries))
        self.assertEqual(result["commits"][self.commits[249]], {"pr_ids": [5], "status": "associated"})
        self.assertEqual(result["pull_requests"]["5"]["status"], "completed")

    def test_the_commit_limit_still_marks_provenance_partial(self):
        result = review_github._origin_prs(FakeGraphQL(), self.commits, max_commits=200)
        self.assertFalse(result["complete"])
        self.assertEqual(result["commits"][self.commits[249]]["status"], "not-queried-limit")

    def test_a_failed_batch_or_unknown_commit_is_a_warning_not_a_silent_gap(self):
        failed = review_github._origin_prs(FakeGraphQL(fail=True), self.commits[:2])
        self.assertFalse(failed["complete"])
        self.assertEqual({row["status"] for row in failed["commits"].values()}, {"query-failed"})
        missing = review_github._origin_prs(FakeGraphQL(missing={self.commits[1]}), self.commits[:2])
        self.assertFalse(missing["complete"])
        self.assertEqual(missing["commits"][self.commits[1]]["status"], "query-failed")
        self.assertEqual(missing["commits"][self.commits[0]]["status"], "associated")


class GraphQLGuardTests(unittest.TestCase):
    def test_mutations_are_refused_before_any_request(self):
        client = review_github.GitHubClient("achieveai", "LmDotnetTools")
        with patch.object(client, "_send") as send, self.assertRaises(ReviewError):
            client.graphql("mutation { addComment }")
        send.assert_not_called()


if __name__ == "__main__":
    unittest.main()
