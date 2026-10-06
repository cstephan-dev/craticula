#!/usr/bin/env python3
"""A conformance driver for the runner's own tests. It speaks the protocol of spec/conformance-runner.md but compiles
nothing: it answers from the expected artifacts (--fixtures) or from a table of canned answers (--answers), and --mode
makes it misbehave in one specific way. Independent of run.py on purpose: it is the proof that a driver in another
program needs nothing from the runner (AC-4)."""
import argparse
import json
import os
import sys
import time
from pathlib import Path

ALL = "postgres,mssql,mysql,oracle,sqlite,mongo,search"


def minify(text):
    """Drop JSON whitespace outside strings so a pretty-printed file fits on one line, literals untouched."""
    out, in_str, esc = [], False, False
    for ch in text:
        if in_str:
            out.append(ch)
            if esc:
                esc = False
            elif ch == "\\":
                esc = True
            elif ch == '"':
                in_str = False
        elif ch == '"':
            in_str = True
            out.append(ch)
        elif ch not in " \t\r\n":
            out.append(ch)
    return "".join(out)


def emit(line):
    sys.stdout.write(line + "\n")
    sys.stdout.flush()


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--fixtures")
    ap.add_argument("--answers", help='JSON file: {id: {"artifact": "<raw JSON text>"} | {"error": {kind, message}}}')
    ap.add_argument("--backends", default=ALL)
    ap.add_argument("--mode", default="ok")
    ap.add_argument("--after", type=int, default=2, help="requests answered before a crash / hang / early exit")
    ap.add_argument("--pid-file", help="write this process's pid here, so a test can check nothing outlives the run")
    args = ap.parse_args()
    if args.pid_file:
        Path(args.pid_file).write_text(str(os.getpid()))
    canned = json.loads(Path(args.answers).read_text()) if args.answers else {}
    backends = json.dumps(args.backends.split(","))

    if args.mode == "hang-hello":
        time.sleep(600)
    if args.mode == "exit-silently":
        sys.exit(0)
    if args.mode == "bad-hello":
        emit('{"notHello":1}')
    elif args.mode == "no-hello":
        pass
    elif args.mode == "hello-garbage":
        emit("this is not json")
    else:
        emit('{"hello":{"impl":"fake","specVersion":"0.0.0","backends":%s}}' % backends)

    n, previous = 0, None
    for line in sys.stdin:
        req = json.loads(line)
        rid, backend = req["id"], req["backend"]
        n += 1
        if args.mode == "crash" and n > args.after:
            sys.stderr.write("fake driver: simulated crash\n")
            sys.exit(1)
        if args.mode == "early-exit" and n > args.after:
            sys.exit(0)
        if args.mode == "hang" and n > args.after:
            time.sleep(600)
        if args.mode == "unsolicited" and n == 2:
            emit('{"id":"nobody#postgres","artifact":{}}')
        if args.mode == "non-json" and n == 2:
            emit("not json at all")
        if args.mode == "not-object" and n == 2:
            emit("[1,2]")

        if rid in canned:
            body = canned[rid]
            payload = '"artifact":' + body["artifact"] if "artifact" in body else '"error":' + json.dumps(body["error"])
        else:
            category, rest = rid.split("/", 1)
            slug = rest.split("#", 1)[0]
            path = Path(args.fixtures) / category / slug / "expected" / (backend + ".artifact.json")
            payload = '"artifact":' + minify(path.read_text(encoding="utf-8-sig"))

        answer_id = rid
        if args.mode == "repeat-id" and n == 2:
            answer_id = previous  # the id already answered: repeated, and out of order
        if args.mode == "unknown-id" and n == 2:
            answer_id = "nope"
        if args.mode == "neither" and n == 2:
            payload = '"note":1'
        if args.mode == "both" and n == 2:
            payload = payload + ',"error":{"kind":"x","message":"y"}'
        emit('{"id":%s,%s}' % (json.dumps(answer_id), payload))
        previous = rid

    if args.mode == "trailing":
        emit('{"id":"late#postgres","artifact":{}}')
    if args.mode == "exit-nonzero":
        sys.exit(3)
    if args.mode == "no-exit":
        time.sleep(600)


if __name__ == "__main__":
    main()
