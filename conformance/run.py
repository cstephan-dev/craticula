#!/usr/bin/env python3
"""Language-neutral conformance runner (spec/conformance-runner.md, TRD T-1 tier a).

Starts a driver command, streams every fixture case to it as JSON Lines, compares each answer with the expected
artifact under the backend's equality rule (spec §4) and reports. Python 3.10+, standard library only.

Exit status: 0 every graded case passed, 1 at least one failed, 2 protocol failure, usage error or an empty graded set
(spec §7).
"""
from __future__ import annotations

import argparse
import fnmatch
import json
import os
import queue
import shlex
import signal
import subprocess
import sys
import threading
import time
from pathlib import Path

BACKENDS = ("postgres", "mssql", "mysql", "oracle", "sqlite", "mongo", "search")
SQL_BACKENDS = ("postgres", "mssql", "mysql", "oracle", "sqlite")
ARTIFACT_SUFFIX = ".artifact.json"
DEFAULT_TIMEOUT = 30.0  # spec §2.4 item 4
MAX_LISTED = 20
POSIX = os.name != "nt"


class UsageError(Exception):
    pass


class ProtocolFailure(Exception):
    pass


# ---------------------------------------------------------------- literal-preserving JSON (spec §4)

class Num:
    """A JSON number kept as written: `1`, `1.0` and `1E0` are three different values."""
    __slots__ = ("text",)

    def __init__(self, text: str) -> None:
        self.text = text


class Obj:
    """A JSON object kept in document order."""
    __slots__ = ("pairs", "map")

    def __init__(self, pairs: list[tuple[str, object]]) -> None:
        self.pairs = pairs
        self.map = dict(pairs)

    def keys(self) -> list[str]:
        return [k for k, _ in self.pairs]


def _reject_constant(name: str) -> object:
    raise ValueError(f"{name} is not valid JSON")


def parse_json(text: str) -> object:
    return json.loads(text, object_pairs_hook=Obj, parse_float=Num, parse_int=Num, parse_constant=_reject_constant)


def dumps(value: object) -> str:
    """Serialise a parsed value on one line, numbers exactly as written. ASCII only, so any string survives a pipe."""
    if isinstance(value, Obj):
        return "{" + ",".join(f"{json.dumps(k)}:{dumps(v)}" for k, v in value.pairs) + "}"
    if isinstance(value, list):
        return "[" + ",".join(dumps(v) for v in value) + "]"
    if isinstance(value, Num):
        return value.text
    return json.dumps(value)


def show(value: object, limit: int = 300) -> str:
    text = "<missing>" if value is MISSING else dumps(value)
    return text if len(text) <= limit else text[:limit] + f"... ({len(text)} chars)"


MISSING = object()


# ---------------------------------------------------------------- equality (spec §4)

def _kind(v: object) -> str:
    if isinstance(v, Obj):
        return "object"
    if isinstance(v, list):
        return "array"
    if isinstance(v, Num):
        return "number"
    if isinstance(v, bool):
        return "boolean"
    if v is None:
        return "null"
    return "string"


def first_diff(exp: object, act: object, path: str, ordered_keys: bool):
    """First place where the two values differ, as (path, expected, actual), or None. Numbers compare by text."""
    ke, ka = _kind(exp), _kind(act)
    if ke != ka:
        return path, show(exp), show(act)
    if ke == "object":
        if ordered_keys:
            ek, ak = exp.keys(), act.keys()
            if ek != ak:
                return path, "keys " + show(ek), "keys " + show(ak)
            for (k, ev), (_, av) in zip(exp.pairs, act.pairs):
                d = first_diff(ev, av, f"{path}.{k}", ordered_keys)
                if d:
                    return d
            return None
        for k, ev in exp.pairs:
            d = first_diff(ev, act.map.get(k, MISSING), f"{path}.{k}", ordered_keys) if k in act.map \
                else (f"{path}.{k}", show(ev), show(MISSING))
            if d:
                return d
        for k, av in act.pairs:
            if k not in exp.map:
                return f"{path}.{k}", show(MISSING), show(av)
        return None
    if ke == "array":
        for i, (ev, av) in enumerate(zip(exp, act)):
            d = first_diff(ev, av, f"{path}[{i}]", ordered_keys)
            if d:
                return d
        if len(exp) != len(act):
            i = min(len(exp), len(act))
            return (f"{path}[{i}]", show(exp[i] if i < len(exp) else MISSING),
                    show(act[i] if i < len(act) else MISSING))
        return None
    if ke == "number":
        return None if exp.text == act.text else (path, show(exp), show(act))
    return None if exp == act and (ke != "boolean" or exp is act) else (path, show(exp), show(act))


def compare(backend: str, exp: object, act: object):
    if backend in SQL_BACKENDS:  # dialect, sql, params by value; member order in the wrapper is not significant
        if not isinstance(exp, Obj) or not isinstance(act, Obj):
            return first_diff(exp, act, "$", True)
        for member in ("dialect", "sql", "params"):
            e, a = exp.map.get(member, MISSING), act.map.get(member, MISSING)
            d = (f"$.{member}", show(e), show(a)) if (e is MISSING or a is MISSING) and e is not a \
                else first_diff(e, a, f"$.{member}", True) if e is not MISSING else None
            if d:
                return d
        return None
    return first_diff(exp, act, "$", ordered_keys=(backend == "mongo"))  # search: OSE-0, unordered maps


# ---------------------------------------------------------------- fixtures (spec §1)

def _out_of_tier(d: Path, graded: bool) -> list[str]:
    """What this directory carries that the tier does not grade — by content, even beside a graded artifact."""
    exp = d / "expected"
    names = {p.name for p in exp.iterdir()} if exp.is_dir() else set()
    reasons = []
    if "rejection.json" in names:
        reasons.append("rejection (SEC-6)")
    if "export.csv" in names or "export.tsv" in names or (d / "rowStream.json").exists():
        reasons.append("writer output (EXP-4)")
    if (d / "dataset.json").exists() or (d / "expectedRows.json").exists():
        reasons.append("execution equality (T-1b)")
    return reasons or ([] if graded else ["no graded file"])


def enumerate_fixtures(root: Path, only: list[str]):
    """Return (cases, out_of_tier): cases = [(fixture_id, backend, dir)], out_of_tier = [(fixture_id, reason)]."""
    if not root.is_dir():
        raise UsageError(f"fixture tree not found: {root}")
    cases, out = [], []
    for cat in sorted(p for p in root.iterdir() if p.is_dir() and p.name != "schemas"):
        for d in sorted(p for p in cat.iterdir() if p.is_dir()):
            fid = f"{cat.name}/{d.name}"
            exp = d / "expected"
            stems = sorted(p.name[: -len(ARTIFACT_SUFFIX)] for p in exp.glob("*" + ARTIFACT_SUFFIX)) if exp.is_dir() else []
            for s in stems:
                if s not in BACKENDS:
                    raise UsageError(f"{fid}: expected/{s}{ARTIFACT_SUFFIX} names a backend outside {', '.join(BACKENDS)}")
            if only and not any(fnmatch.fnmatchcase(fid, g) or any(fnmatch.fnmatchcase(f"{fid}#{s}", g) for s in stems)
                                for g in only):
                continue
            out += [(fid, r) for r in _out_of_tier(d, bool(stems))]
            for s in sorted(stems, key=BACKENDS.index):
                if not only or any(fnmatch.fnmatchcase(fid, g) or fnmatch.fnmatchcase(f"{fid}#{s}", g) for g in only):
                    cases.append((fid, s, d))
    if not cases:  # spec §6: an empty graded set never passes
        raise UsageError(f"no expected artifact matches --only {' '.join(only)}" if only
                         else f"no expected artifact under {root}")
    return cases, out


def _read_json(path: Path) -> object:
    try:
        return parse_json(path.read_text(encoding="utf-8-sig"))
    except (OSError, ValueError) as e:
        raise UsageError(f"cannot read {path}: {e}") from e


# ---------------------------------------------------------------- driver session (spec §2)

def driver_argv(command: str, posix: bool = POSIX) -> list[str] | str:
    """Spec §9: no shell. POSIX splits like a shell line; Windows parses the string itself (shlex eats backslashes)."""
    return shlex.split(command) if posix else command


class Driver:
    def __init__(self, command: str, timeout: float) -> None:
        try:
            argv = driver_argv(command)
            if not argv:
                raise ValueError("empty command")
            # its own process group, so kill() reaches whatever a launcher driver (dotnet run, npx …) started
            self.proc = subprocess.Popen(argv, stdin=subprocess.PIPE, stdout=subprocess.PIPE,  # stderr passes through
                                         start_new_session=POSIX,
                                         creationflags=0 if POSIX else subprocess.CREATE_NEW_PROCESS_GROUP)
        except (OSError, ValueError) as e:
            raise UsageError(f"cannot start driver {command!r}: {e}") from e
        self.timeout = timeout
        self._lines: queue.Queue[bytes | None] = queue.Queue()
        self._out: queue.Queue[bytes | None] = queue.Queue()
        self._reader = threading.Thread(target=self._read, daemon=True)
        self._writer = threading.Thread(target=self._write, daemon=True)
        self._reader.start()
        self._writer.start()

    def _read(self) -> None:
        for raw in iter(self.proc.stdout.readline, b""):
            self._lines.put(raw)
        self._lines.put(None)

    def _write(self) -> None:
        try:
            while (data := self._out.get()) is not None:
                self.proc.stdin.write(data)
                self.proc.stdin.flush()
            self.proc.stdin.close()
        except OSError:
            pass  # the reader reports the driver's exit

    def send(self, line: str) -> None:
        self._out.put(line.encode("ascii") + b"\n")

    def read(self, deadline: float, what: str) -> object:
        try:
            raw = self._lines.get(timeout=max(deadline - time.monotonic(), 0))
        except queue.Empty:
            raise ProtocolFailure(f"no {what} within {self.timeout:g} s") from None
        if raw is None:
            try:
                status = self.proc.wait(timeout=2)
            except subprocess.TimeoutExpired:
                status = "?"
            raise ProtocolFailure(f"driver stopped before the {what} (exit status {status})")
        try:
            value = parse_json(raw.decode("utf-8"))
        except ValueError as e:
            raise ProtocolFailure(f"stdout line is not JSON ({e}): {raw[:80]!r}") from None
        if not isinstance(value, Obj):
            raise ProtocolFailure(f"stdout line is not a JSON object: {raw[:80]!r}")
        return value

    def hello(self) -> dict:
        v = self.read(time.monotonic() + self.timeout, "hello")
        h = v.map.get("hello")
        if not isinstance(h, Obj):
            raise ProtocolFailure("first line is not a hello")
        impl, spec, backends = h.map.get("impl"), h.map.get("specVersion"), h.map.get("backends")
        if not (isinstance(impl, str) and isinstance(spec, str) and isinstance(backends, list) and backends
                and all(isinstance(b, str) and b in BACKENDS for b in backends)):
            raise ProtocolFailure("hello needs impl, specVersion and a non-empty backends list from "
                                  + ", ".join(BACKENDS))
        return {"impl": impl, "specVersion": spec, "backends": list(dict.fromkeys(backends))}

    def ask(self, request: str, rid: str) -> Obj:
        self.send(request)
        v = self.read(time.monotonic() + self.timeout, "response")
        if v.map.get("id") != rid:
            raise ProtocolFailure(f"response id {show(v.map.get('id', MISSING))} where {rid!r} was awaited")
        has_a, has_e = "artifact" in v.map, "error" in v.map
        err = v.map.get("error")
        if has_a == has_e or (has_e and not (isinstance(err, Obj) and isinstance(err.map.get("kind"), str)
                                             and isinstance(err.map.get("message"), str))):
            raise ProtocolFailure(f"response to {rid!r} has neither a lone artifact nor a valid error")
        return v

    def finish(self) -> None:
        """Close stdin; the driver must exit 0 and say nothing more (spec §2.3, §2.4)."""
        self._out.put(None)
        deadline = time.monotonic() + self.timeout
        try:
            extra = self._lines.get(timeout=max(deadline - time.monotonic(), 0))
            if extra is not None:
                raise ProtocolFailure(f"unexpected line after the last response: {extra[:80]!r}")
            status = self.proc.wait(timeout=max(deadline - time.monotonic(), 0.1))
        except (queue.Empty, subprocess.TimeoutExpired):
            raise ProtocolFailure(f"driver did not exit within {self.timeout:g} s of the end of the session") from None
        if status != 0:
            raise ProtocolFailure(f"driver exited with status {status}")

    def kill(self) -> None:
        """End the driver and every process it started (spec §2.4), then release the pipes nobody is blocked on."""
        if self.proc.returncode is None:  # not reaped yet, so its pid still names the driver's group / tree
            try:
                if POSIX:
                    os.killpg(self.proc.pid, signal.SIGKILL)
                else:
                    subprocess.run(["taskkill", "/T", "/F", "/PID", str(self.proc.pid)],
                                   stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, check=False)
            except OSError:
                pass
            if self.proc.poll() is None:
                self.proc.kill()
        self.proc.wait()
        self._out.put(None)  # the writer closes stdin and ends
        for thread, pipe in ((self._writer, self.proc.stdin), (self._reader, self.proc.stdout)):
            thread.join(timeout=1)
            if not thread.is_alive():  # closing a pipe under a thread blocked on it would block us too
                try:
                    pipe.close()
                except (OSError, ValueError):
                    pass


# ---------------------------------------------------------------- run and report

def run(args: argparse.Namespace) -> tuple[int, dict]:
    root = Path(args.fixtures)
    cases, out = enumerate_fixtures(root, args.only or [])
    report: dict = {"driver": args.driver, "status": "pass", "failures": [], "unanswered": [], "protocolFailure": None,
                    "outOfTier": [{"id": i, "reason": r} for i, r in out], "totals": {}, "hello": None}
    jobs = []  # read every file first: an unreadable fixture is a usage error, never a half-run session
    for fid, backend, d in cases:
        rid = f"{fid}#{backend}"
        request = dumps(Obj([("id", rid), ("backend", backend), ("schema", _read_json(d / "schema.json")),
                             ("agGridRequest", _read_json(d / "agGridRequest.json"))]))
        jobs.append((rid, backend, request, _read_json(d / "expected" / f"{backend}{ARTIFACT_SUFFIX}")))
    driver = Driver(args.driver, args.timeout)
    graded: list = []
    answered = 0
    try:
        hello = report["hello"] = driver.hello()
        declared = set(hello["backends"])
        totals = {b: {"passed": 0, "failed": 0, "notGraded": 0} for b in BACKENDS}
        report["totals"] = totals
        graded = [j for j in jobs if j[1] in declared]
        for _, b, _r, _e in jobs:
            if b not in declared:
                totals[b]["notGraded"] += 1
        for rid, backend, request, expected in graded:
            resp = driver.ask(request, rid)
            answered += 1
            if "error" in resp.map:
                e = resp.map["error"].map
                diff = None
                failure = {"id": rid, "error": f"{e['kind']}: {e['message']}"}
            else:
                diff = compare(backend, expected, resp.map["artifact"])
                failure = diff and {"id": rid, "path": diff[0], "expected": diff[1], "actual": diff[2]}
            if failure:
                totals[backend]["failed"] += 1
                report["failures"].append(failure)
            else:
                totals[backend]["passed"] += 1
        driver.finish()
    except ProtocolFailure as e:
        report["status"] = "protocol-failure"
        report["protocolFailure"] = str(e)
        report["unanswered"] = [j[0] for j in graded[answered:]]
        return 2, report
    finally:
        driver.kill()
    if not graded:  # spec §6: no selected case is in a backend the driver declared
        report["status"] = "nothing-graded"
        return 2, report
    if report["failures"]:
        report["status"] = "fail"
        return 1, report
    return 0, report


def render(report: dict) -> str:
    lines: list[str] = []
    h = report["hello"]
    if h:
        lines.append(f"driver: impl={h['impl']} specVersion={h['specVersion']} backends={','.join(h['backends'])}")
    for f in report["failures"]:
        lines.append(f"FAIL {f['id']}")
        if "error" in f:
            lines.append(f"  driver answered error {f['error']}")
        else:
            lines += [f"  at       {f['path']}", f"  expected {f['expected']}", f"  actual   {f['actual']}"]
    if report["protocolFailure"]:
        lines.append(f"PROTOCOL FAILURE: {report['protocolFailure']}")
        un = report["unanswered"]
        if un:
            lines.append(f"unanswered requests ({len(un)}):")
            lines += [f"  {i}" for i in un[:MAX_LISTED]]
            if len(un) > MAX_LISTED:
                lines.append(f"  ... and {len(un) - MAX_LISTED} more")
    if report["totals"]:
        lines.append(f"{'backend':<10}{'passed':>8}{'failed':>8}{'not graded':>12}")
        for b, t in report["totals"].items():
            if t["passed"] or t["failed"] or t["notGraded"]:
                lines.append(f"{b:<10}{t['passed']:>8}{t['failed']:>8}{t['notGraded']:>12}")
    if report["outOfTier"]:
        lines.append(f"out of tier ({len(report['outOfTier'])}):")
        lines += [f"  {o['id']}  {o['reason']}" for o in report["outOfTier"]]
    lines.append({"pass": "RESULT: PASS", "fail": "RESULT: FAIL", "protocol-failure": "RESULT: PROTOCOL FAILURE",
                  "nothing-graded": "RESULT: NOTHING GRADED (no selected case is in a declared backend)"}
                 [report["status"]])
    return "\n".join(lines)


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description="Grade an implementation's driver against the conformance fixtures.")
    ap.add_argument("--driver", required=True,
                    help='command that starts the driver, e.g. "dotnet driver.dll" (spec §9)')
    ap.add_argument("--fixtures", default=str(Path(__file__).resolve().parent.parent / "spec" / "fixtures"))
    ap.add_argument("--json", metavar="FILE", help="also write the report as JSON")
    ap.add_argument("--only", action="append", metavar="GLOB",
                    help="grade only fixtures matching <category>/<slug> or <category>/<slug>#<backend> (repeatable)")
    ap.add_argument("--timeout", type=float, default=DEFAULT_TIMEOUT, help="seconds per hello/response (default 30)")
    args = ap.parse_args(argv)
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(errors="backslashreplace")
    try:
        code, report = run(args)
    except UsageError as e:
        print(f"usage error: {e}", file=sys.stderr)
        return 2
    print(render(report))
    if args.json:
        try:
            Path(args.json).write_text(json.dumps(report, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
        except OSError as e:
            print(f"usage error: cannot write {args.json}: {e}", file=sys.stderr)
            return 2
    return code


if __name__ == "__main__":
    sys.exit(main())
