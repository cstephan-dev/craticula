Proves AR-6's `blank` operator on a `string` field: SEM-2 pins `blank` as `IS NULL OR = ''` specifically for text fields (unlike non-text types, which are `IS NULL` alone). That is the *field-type* half of the rule and is what this Postgres-only case pins; the *engine* half is **SQL-7** — on a backend whose empty string is itself NULL (Oracle) the text arm collapses to `IS NULL` too, and `dialect-parity/019` is where that is pinned.

*Search-arm correction (#1644, the story that compiled this arm for the first time):* the
empty-string `term` was spelled with the bare shorthand `{"status.keyword": ""}` and is now
`{"status.keyword": {"value": ""}}`. **OSE-1** spells every `term` on a `string` field in the long
form, keyed on the field's declared TYPE rather than on whether a parameter happens to be present, so
one field has one spelling whatever its `caseSensitivity` — the form GRP-2 pins by hand for a
drill-down and `dialect-parity/014` uses for the case-sensitive opt-in. It still carries no
`case_insensitive`: this value is the sentinel SEM-2's own definition of `blank` mints, not a client
value, and an empty string has no case to fold (**OSE-4**).
