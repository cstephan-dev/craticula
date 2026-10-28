# ADR-0004: The decision records are published, one folder, and only from a fixed point on

Status: Proposed   Date: 2026-10-28
Work items: #1665   Classification: Public

The maintainer accepts this record. It was written for readers outside the project: it names no private path or host.

## Context

- The private repository is the source of truth. The publish script (kept in the private repository) filters it, removes the
  private paths, and pushes the result to `pluries/craticula`, never by force and always as a fast-forward of the public branch.
- Until now the whole `docs` folder was private. Contributors could not read why the design is the way it is, and the license
  check looks for an accepted decision record in `docs/adr/` before it lets a case-by-case license through.
- The spec asks for exactly one folder under `docs` to be public: the decision records (FR-12).
- The filter rewrites commits. A commit that touched an ADR was published without it. If `docs/adr/` were simply un-privated for
  all of history, every filtered commit that touched an ADR would change its hash, the public tip would no longer be an ancestor of
  the new head, and the publish would refuse (or need a force push, which is ruled out).

## Decision

1. `docs` stays private, except the folder `docs/adr/`.
2. The exception applies only to commits **after a fixed commit**: the private commit whose filtered form is the public tip of
   2026-10-28. Commits at or before it drop `docs/adr/` exactly as they did, so no public hash changes and the next publish is
   a plain fast-forward.
3. An ADR therefore reaches the public repository the first time a later commit changes it. The existing records were edited once
   for this reason (and to make them read well outside the project).
4. A record that is published is marked `Classification: Public` and written for outsiders: no private path, host, person or
   internal process. A test checks every record with that mark.
5. The publish keeps two guards for the private paths: after filtering, any path under `docs` other than `docs/adr/` in the history
   stops the publish; and the added lines of every published commit, ADRs included, go through the same secret scan as the rest.
6. A pull request from outside that edits a record is not re-applied by the script that brings public changes back (it refuses any
   `docs` path, which is the safe default). The maintainer applies such a change by hand after reading it.

## Alternatives

- **Un-private `docs/adr/` for all of history.** Rewrites the hash of every filtered commit that touched an ADR; the public tip stops
  being an ancestor and the publish refuses. Rejected.
- **Copy the records to a public folder outside `docs`.** Two places for one decision, and the license check and the spec both name
  `docs/adr/`. Rejected.
- **Keep every record private.** Contributors cannot see decisions that affect them (the mirror's merge commits, the demo's
  evaluation-mode dependency). Rejected.

## Consequences

- Positive: the decision records are visible; the license check can find an accepted record on the public side; no public hash
  changes; the push stays a fast-forward.
- Negative: the fixed commit is a constant in the publish script; a record published later must change in a commit after it to enter;
  a mistake in a record is public at once, and history cannot be rewritten on a mirror, so it is fixed forward.
- Follow-up: when a record names a case-by-case license (FR-09), publish it the same way.

## Evidence

On 2026-10-28, in scratch copies (no push): the old and the new filter print the same tip for the unchanged history; one new commit
that changes two ADRs and another document under `docs` publishes the two records in full and not the other document; the
public tip stays an ancestor of the new head.

## Compliance

- No personal data and no secret. The records are public by design.
- Risk: a record that names a host, a client or a credential. Mitigation: the rule in point 4, the secret scan in point 5, and a read of
  each record before it is first published.
