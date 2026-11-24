import sys
from pathlib import Path
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from check_repository import check_push, file_error, subject_error, verify_release_tag


class RepositoryPolicyTests(unittest.TestCase):
    def test_source_assets_and_lockfiles_are_allowed(self):
        for path in ["docs/image with spaces.png", "desktop/CrossPlatform/packages.lock.json", "README.md"]:
            self.assertIsNone(file_error(path, b"ordinary source"))

    def test_binaries_site_data_and_unknown_root_are_rejected(self):
        for path in ["desktop/bin/app.exe", "docs/credentials.pem", "docs/site.bacnet.json", "datalink/private.go", "notes.tmp"]:
            self.assertIsNotNone(file_error(path, b"content"))

    def test_likely_secrets_and_large_assets_are_rejected_without_echoing_them(self):
        for secret in [("ghp" + "_" + "A" * 36).encode(), ("-----BEGIN " + "PRIVATE KEY-----").encode()]:
            error = file_error("docs/example.txt", secret)
            self.assertIsNotNone(error)
            self.assertNotIn(secret.decode(), error)
        self.assertIsNotNone(file_error("docs/large.png", b"x" * (2 * 1024 * 1024 + 1)))

    def test_conventional_subjects(self):
        for subject in ["feat: compare command priorities", "fix(snapshot)!: reject stale identity", "ci: enforce repository checks"]:
            self.assertIsNone(subject_error(subject))
        for subject in ["updates", "feat:", "fix: " + "x" * 100]:
            self.assertIsNotNone(subject_error(subject))

    def test_main_and_existing_tag_changes_are_blocked(self):
        zero = "0" * 40
        oid = "a" * 40
        for line in [f"refs/heads/main {oid} refs/heads/main {oid}",
                     f"refs/tags/v1.2.3 {oid} refs/tags/v1.2.3 {oid}",
                     f"(delete) {zero} refs/tags/v1.2.3 {oid}"]:
            with self.assertRaises(ValueError):
                check_push([line])
        check_push([f"refs/heads/codex/test {oid} refs/heads/codex/test {zero}"])

    def test_release_rejects_invalid_semver_and_lightweight_tags(self):
        for tag in ["v01.2.3", "1.2.3", "v1.2.3-01", "v1.2"]:
            with self.assertRaises(ValueError):
                verify_release_tag(tag)
        with patch("check_repository.git", return_value=b"commit\n"):
            with self.assertRaisesRegex(ValueError, "annotated"):
                verify_release_tag("v1.2.3")

    def test_annotated_release_requires_changelog_on_tagged_commit(self):
        with patch("check_repository.git", side_effect=[b"tag", b"a" * 40, b"# Changelog\n"]), patch("check_repository.subprocess.run"):
            with self.assertRaisesRegex(ValueError, "dated changelog"):
                verify_release_tag("v1.2.3")
        with patch("check_repository.git", side_effect=[b"tag", b"a" * 40, b"## [1.2.3] - 2026-11-24\n"]), patch("check_repository.subprocess.run"):
            verify_release_tag("v1.2.3")


if __name__ == "__main__":
    unittest.main()
