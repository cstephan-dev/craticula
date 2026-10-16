#!/usr/bin/env python3
"""Unit tests for scripts/check-community-oracle.py (spec/fixtures/community-infinite, AG-7)."""
import importlib.util
import json
import pathlib
import shutil
import sys
import tempfile
import unittest

HERE = pathlib.Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location("oracle", HERE / "check-community-oracle.py")
oracle = importlib.util.module_from_spec(spec)
spec.loader.exec_module(oracle)


class RepositoryState(unittest.TestCase):
    def test_every_case_equals_its_oracle(self):
        mapping = json.loads((oracle.CATEGORY / "oracle.json").read_text())
        self.assertEqual([], oracle.check(mapping))

    def test_every_case_is_labelled_derived_or_captured(self):
        for case in sorted(p for p in oracle.CATEGORY.iterdir() if p.is_dir()):
            first = (case / "description.md").read_text().lstrip()
            self.assertTrue(first.startswith("**derived**") or first.startswith("**captured**"), case.name)


class Mutations(unittest.TestCase):
    def setUp(self):
        self.tmp = pathlib.Path(tempfile.mkdtemp())
        self.addCleanup(shutil.rmtree, self.tmp, True)
        self.saved = (oracle.ROOT, oracle.CATEGORY)
        oracle.ROOT = self.tmp
        oracle.CATEGORY = self.tmp / "community-infinite"
        self.addCleanup(lambda: setattr(oracle, "ROOT", self.saved[0]))
        self.addCleanup(lambda: setattr(oracle, "CATEGORY", self.saved[1]))
        src = self.tmp / "operators" / "009-x"
        (src / "expected").mkdir(parents=True)
        for name in oracle.FILES:
            (src / name).write_text("{}")
        (src / "expected" / "postgres.artifact.json").write_text('{"a": 1}')
        (src / "agGridRequest.json").write_text(json.dumps({"startRow": 0, "pivotMode": False, "filterModel": None}))
        shutil.copytree(src, oracle.CATEGORY / "001-case")
        (oracle.CATEGORY / "001-case" / "agGridRequest.json").write_text(json.dumps({"startRow": 0, "filterModel": None}))
        self.mapping = {"001-case": "operators/009-x"}

    def test_equal_case_passes(self):
        self.assertEqual([], oracle.check(self.mapping))

    def test_a_changed_artifact_fails(self):
        (oracle.CATEGORY / "001-case" / "expected" / "postgres.artifact.json").write_text('{"a": 2}')
        self.assertEqual(1, oracle.check(self.mapping)[0][0])

    def test_a_missing_artifact_fails(self):
        (oracle.CATEGORY / "001-case" / "expected" / "postgres.artifact.json").unlink()
        self.assertEqual(1, oracle.check(self.mapping)[0][0])

    def test_a_request_that_differs_beyond_the_dropped_fields_fails(self):
        (oracle.CATEGORY / "001-case" / "agGridRequest.json").write_text(json.dumps({"startRow": 5, "filterModel": None}))
        self.assertEqual(1, oracle.check(self.mapping)[0][0])

    def test_a_missing_source_is_reported_as_2(self):
        self.assertEqual(2, oracle.check({"001-case": "operators/nope"})[0][0])

    def test_a_v36_payload_equals_the_derived_request(self):
        derived = {"startRow": 0, "endRow": 100, "filterModel": None, "sortModel": [{"colId": "id", "sort": "asc"}]}
        captured = {"startRow": 0, "endRow": 100, "filterModel": {}, "sortModel": [{"colId": "id", "sort": "asc", "type": "default"}]}
        self.assertEqual(oracle.canonical(derived), oracle.canonical(captured))

    def test_midnight_on_a_date_is_the_same_value(self):
        self.assertEqual(
            oracle.canonical({"filterModel": {"d": {"dateFrom": "2026-01-01 00:00:00"}}}),
            oracle.canonical({"filterModel": {"d": {"dateFrom": "2026-01-01"}}}))
        self.assertNotEqual(
            oracle.canonical({"filterModel": {"d": {"dateFrom": "2026-01-01 05:00:00"}}}),
            oracle.canonical({"filterModel": {"d": {"dateFrom": "2026-01-01"}}}))

    def test_a_folder_without_a_mapping_is_reported(self):
        (oracle.CATEGORY / "002-new").mkdir()
        self.assertEqual(2, max(code for code, _ in oracle.check(self.mapping)))


class Normalise(unittest.TestCase):
    def test_legacy_two_conditions_become_the_conditions_array(self):
        legacy = {"operator": "OR", "condition1": {"type": "a"}, "condition2": {"type": "b"}}
        self.assertEqual({"x": 1, "operator": "OR", "conditions": [{"type": "a"}, {"type": "b"}], "filterModel": {}}, oracle.canonical({"x": 1, **legacy}))


if __name__ == "__main__":
    sys.exit(unittest.main())
