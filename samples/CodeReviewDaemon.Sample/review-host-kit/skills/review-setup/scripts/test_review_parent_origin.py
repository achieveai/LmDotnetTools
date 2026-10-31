"""Which parent-repository origins the review store may push its record to."""
import subprocess
import tempfile
import unittest
from pathlib import Path

from review_common import ReviewError, validate_parent_origin_remote

ADO_SOURCE = "https://dev.azure.com/org/Proj/_git/Source"
GITHUB_SOURCE = "https://github.com/achieveai/LmDotnetTools.git"


class ParentOriginTests(unittest.TestCase):
    def store(self, origin: str, *sources: str) -> Path:
        scratch = tempfile.TemporaryDirectory(ignore_cleanup_errors=True)
        self.addCleanup(scratch.cleanup)
        root = Path(scratch.name)
        git = lambda *args: subprocess.run(["git", "-C", str(root), *args], check=True, capture_output=True)
        git("init", "-q")
        git("config", "remote.origin.url", origin)
        for number, url in enumerate(sources):
            git("config", "--file", ".gitmodules", f"submodule.s{number}.path", f"repos/S{number}")
            git("config", "--file", ".gitmodules", f"submodule.s{number}.url", url)
        return root

    def test_ado_hosted_store_reviewing_ado_sources_may_push(self):
        origin = "https://dev.azure.com/org/Proj/_git/Reviews"
        self.assertEqual(validate_parent_origin_remote(self.store(origin, ADO_SOURCE)), origin)

    def test_github_hosted_store_reviewing_github_sources_may_push(self):
        origin = "https://github.com/achieveai/AchieveAiReviews.git"
        self.assertEqual(validate_parent_origin_remote(self.store(origin, GITHUB_SOURCE)), origin)

    def test_origin_naming_a_configured_source_is_refused(self):
        for origin, sources in (("https://dev.azure.com/ORG/proj/_git/SOURCE", (ADO_SOURCE,)),
                                ("https://github.com/AchieveAi/lmdotnettools", (ADO_SOURCE, GITHUB_SOURCE))):
            with self.subTest(origin=origin), self.assertRaises(ReviewError):
                validate_parent_origin_remote(self.store(origin, *sources))

    def test_same_repository_name_in_another_project_may_push(self):
        origin = "https://dev.azure.com/org/Other/_git/Source"
        self.assertEqual(validate_parent_origin_remote(self.store(origin, ADO_SOURCE)), origin)


if __name__ == "__main__":
    unittest.main()
