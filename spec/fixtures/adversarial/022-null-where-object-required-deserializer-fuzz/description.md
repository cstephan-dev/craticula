# 022 — `null` entry inside `rowGroupCols` where a `ColumnVO` object is required

## What makes this adversarial
`rowGroupCols` is an array of `ColumnVO` objects (`{ id, displayName, field?, aggFunc? }`).
This request's array is `[null]` — a bare JSON `null` in a slot the wire contract always
populates with an object. This differs from `filterModel: null` (a documented, valid way to say
"no filter") or from an absent field entirely: here the *array element itself*, which the schema
never permits as null, is null — a class a naive deserializer might silently coerce into an
empty/default `ColumnVO` (masking the malformed request) rather than rejecting outright. SEC-6
groups this under "deserializer fuzzing" alongside 021.

## Which validation stage must catch it
**Request parsing.** The row-group column list fails structural parsing before the adapter
attempts to resolve any group column's `id` against the allowlist — there is no colId to even
look up.

## Requirement ids
SEC-6 (deserializer fuzzing representative), AR-8 (group spec expects well-formed group columns).
