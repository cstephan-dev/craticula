#!/usr/bin/env python3
"""Tests scripts/release_notes.py: the line is built from spec/VERSION and the fixture folders,
appended once, and a bad version or an empty suite is refused. Scratch directories only, no network.
Run: python3 scripts/test_release_notes.py (also run by Craticula.Conformance.Tests/ReleaseNotesTests.cs on `dotnet test`).
"""
import tempfile
import unittest
from pathlib import Path

import release_notes as rn

ROOT = Path(__file__).resolve().parent.parent
DOT = "·"


def scratch(version="0.1.0", fixtures=3):
    tmp = Path(tempfile.mkdtemp())
    (tmp / "VERSION").write_text(version + "\n", encoding="utf-8")
    for i in range(fixtures):
        (tmp / "fixtures" / f"fam{i % 2}" / f"{i:03d}-case").mkdir(parents=True)
    (tmp / "fixtures" / "fam0").mkdir(parents=True, exist_ok=True)
    (tmp / "fixtures" / "fam0" / "stray.json").write_text("{}", encoding="utf-8")
    return tmp


class ReleaseNotesTests(unittest.TestCase):
    def run_main(self, tmp, notes_text=None):
        notes = tmp / "notes.md"
        if notes_text is not None:
            notes.write_text(notes_text, encoding="utf-8")
        code = rn.main(["--spec-version-file", str(tmp / "VERSION"), "--fixtures", str(tmp / "fixtures"),
                        "--append-to", str(notes)])
        return code, (notes.read_text(encoding="utf-8") if notes.exists() else None)

    def test_line_shape(self):
        self.assertEqual(f"Spec version 0.1.0 {DOT} conformance fixture suite: 3 fixtures", rn.build_line("0.1.0", 3))

    def test_counts_only_case_folders_two_levels_down(self):
        self.assertEqual(3, rn.count_fixtures(scratch(fixtures=3) / "fixtures"))

    def test_appends_after_blank_line_then_second_run_adds_nothing(self):
        tmp = scratch()
        code, first = self.run_main(tmp, "## Changes\n- a fix\n")
        self.assertEqual(0, code)
        self.assertEqual(f"## Changes\n- a fix\n\nSpec version 0.1.0 {DOT} conformance fixture suite: 3 fixtures\n", first)
        code, second = self.run_main(tmp)
        self.assertEqual(0, code)
        self.assertEqual(first, second)

    def test_existing_spec_version_line_is_kept_as_is(self):
        tmp = scratch()
        old = "notes\n\nSpec version 0.0.9 old\n"
        self.assertEqual((0, old), self.run_main(tmp, old))

    def test_empty_and_missing_notes_work(self):
        code, text = self.run_main(scratch(), "")
        self.assertEqual((0, f"Spec version 0.1.0 {DOT} conformance fixture suite: 3 fixtures\n"), (code, text))
        code, text = self.run_main(scratch())
        self.assertEqual(0, code)
        self.assertTrue(text.startswith("Spec version 0.1.0"))

    def test_bad_version_refused_and_notes_untouched(self):
        for bad in ("1.0", "v1.0.0", "1.0.0-rc1", "", "1.0.0\n2.0.0"):
            tmp = scratch(version=bad)
            self.assertEqual((1, "keep\n"), self.run_main(tmp, "keep\n"), bad)

    def test_zero_fixtures_refused(self):
        tmp = scratch(fixtures=0)
        self.assertEqual((1, "keep\n"), self.run_main(tmp, "keep\n"))

    def test_real_repo_has_a_positive_count_and_valid_version(self):
        rn.build_line(rn.read_spec_version(ROOT / "spec/VERSION"), rn.count_fixtures(ROOT / "spec/fixtures"))


if __name__ == "__main__":
    unittest.main()
