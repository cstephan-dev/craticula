# Conformance runner

`run.py` grades an implementation of Craticula against the fixture suite in [`spec/fixtures/`](../spec/fixtures/README.md).
You give it a **driver** (a small program that compiles what it is sent); it sends every fixture case, compares the
answers with the expected artifacts and prints what differs. Python 3.10+, standard library only.

The contract — protocol, equality rules, coverage, exit codes — is [`spec/conformance-runner.md`](../spec/conformance-runner.md).
This file only says how to use it.

## Run it

```bash
python3 conformance/run.py --driver "dotnet path/to/driver.dll"                  # the whole suite
python3 conformance/run.py --driver "…" --only 'operators/*' --json report.json  # one category, report for tools
```

The options are in [§9 of the spec](../spec/conformance-runner.md#9-running-the-runner), the exit codes in §7;
`python3 conformance/run.py --help` lists them too. A failure prints the case id, the first JSON path where the two
sides differ, and both values.

## Write a driver for a new language

A driver is a loop over stdin and stdout. Follow [§2, §3 and §8 of the spec](../spec/conformance-runner.md):

1. Print the hello line first: `{"hello":{"impl":"…","specVersion":"…","backends":[…]}}`. Declare only the backends you implement; the rest are reported as *not graded*.
2. For each request line, compile `agGridRequest` against `schema` for `backend` with the pinned inputs of §3, and print `{"id":…,"artifact":…}` or `{"id":…,"error":{"kind":…,"message":…}}`.
3. Flush after every line, log to stderr only, and exit 0 when stdin closes.

[`tests/fake_driver.py`](tests/fake_driver.py) is a complete driver that answers from the expected files (its `--mode`
flags make it misbehave for the runner's own tests). Nothing in the runner is specific to a language.

## Test the runner

```bash
python3 -m unittest discover conformance/tests
```

One test per equality rule and per protocol failure of the spec, plus a run over the whole real fixture tree.
