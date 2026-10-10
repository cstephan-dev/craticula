# Export row limit — default max 1,000,000 rows (documented expectation)

**Proves:** EXP-7 ("Max export rows (default 1,000,000) and a cancellation/timeout hook exposed
by every execution adapter"), as a documented expectation — not literally exercised, since
producing (or even declaring) a dataset with over a million rows is not something a hand-authored
seed does (`dataset.json`/`expectedRows.json` stay out of this fixture, `spec/semantics.md` XEQ-1).

## Scenario

`exportFormat: "csv"`, `exportScope: "all"` — the same "export everything" shape as fixture
006/009, compiling to `page: { mode: "none" }` (EXP-2). No filter narrows the result, which is
exactly the situation where an unbounded export could run away without a cap.

## Key expectation — the cap is not, and cannot be, a compiled-artifact field

`ir.schema.json`'s `nonePage` `$def` carries no row-count bound (by design — see its own
description: it "compile[s] the full filtered+sorted artifact with no row-range clause"). The
compiled artifact below is therefore identical in shape to any other `exportScope: "all"`
artifact; it has no field that could encode a row cap. EXP-7's 1,000,000-row default and its
cancellation/timeout hook are **execution-adapter runtime concerns**: every streaming execution
adapter (EXP-3) MUST count rows as they stream past and abort/cancel once the configured cap
(default 1,000,000) is exceeded, and MUST expose a cancellation/timeout hook a host can invoke
independently. Neither of those runtime behaviors is expressible in, or asserted by, this
fixture's compiled artifact — this fixture exists solely to record the documented default for
whichever execution-adapter test suite eventually exercises it against a real dataset.
