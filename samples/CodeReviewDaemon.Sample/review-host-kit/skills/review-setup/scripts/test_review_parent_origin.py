"""Which parent-repository origins the review store may push its record to."""
import subprocess
import tempfile
import unittest
from pathlib import Path

from review_common import ReviewError, validate_parent_origin_remote

ADO_SOURCE = "https://dev.azure.com/org/Proj/_git/Source"
GITHUB_SOURCE = "https://github.com/achieveai/LmDotnetTools.git"


class ParentOriginTests(unittest.TestCase):
    def store(self, origin: str, *sources: str, config: tuple[tuple[str, str], ...] = ()) -> Path:
        scratch = tempfile.TemporaryDirectory(ignore_cleanup_errors=True)
        self.addCleanup(scratch.cleanup)
        root = Path(scratch.name)
        git = lambda *args: subprocess.run(["git", "-C", str(root), *args], check=True, capture_output=True)
        git("init", "-q")
        git("config", "remote.origin.url", origin)
        for key, value in config:
            git("config", key, value)
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

    def test_same_repository_name_under_another_owner_or_org_may_push(self):
        for origin, source in (("https://github.com/someone-else/LmDotnetTools.git", GITHUB_SOURCE),
                               ("https://dev.azure.com/otherorg/Proj/_git/Source", ADO_SOURCE)):
            with self.subTest(origin=origin):
                self.assertEqual(validate_parent_origin_remote(self.store(origin, source)), origin)

    def test_push_destination_rewritten_to_a_source_is_refused(self):
        origin = "https://dev.azure.com/org/Proj/_git/Reviews"
        for config in ((("remote.origin.pushurl", ADO_SOURCE),),
                       ((f"url.{ADO_SOURCE}.pushInsteadOf", origin),),
                       ((f"url.{ADO_SOURCE}.insteadOf", origin),)):
            with self.subTest(config=config[0][0]), self.assertRaises(ReviewError):
                validate_parent_origin_remote(self.store(origin, ADO_SOURCE, config=config))

    def test_push_destination_rewritten_to_another_store_url_may_push(self):
        origin = "https://dev.azure.com/org/Proj/_git/Reviews"
        mirror = "https://dev.azure.com/org/Proj/_git/ReviewsMirror"
        store = self.store(origin, ADO_SOURCE, config=(("remote.origin.pushurl", mirror),))
        self.assertEqual(validate_parent_origin_remote(store), origin)


if __name__ == "__main__":
    unittest.main()
