# 013 — malformed date string against a `date` field

## What makes this adversarial
`createdDate` is declared `type: "date"` in `schema.json`. The date filter's `dateFrom`
is sent as `"2026-13-45"` — syntactically shaped like the AG Grid date format
(`"YYYY-MM-DD"`, per `spec/fixtures/README.md`'s wire-shape note) but semantically not a
valid calendar date (month 13, day 45). A naive parser using lenient date-rollover arithmetic
could silently normalize this into *some* valid date, which would be exactly the "silent
full-table match"-adjacent failure SEM-5 forbids; Strict mode requires an outright rejection.

## Which validation stage must catch it
**Type coercion.** `createdDate` is allowlisted and the single condition is far under every
SEC-3 limit; only the date-parsing/coercion step fails.

## Requirement ids
SEM-5 (type coercion, Strict mode), SEM-4 (dates & zones — the adapter parses using the field's
declared type), AR-7.
