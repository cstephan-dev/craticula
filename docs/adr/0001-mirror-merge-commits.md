# ADR-0001: The public mirror holds merge commits so Release Please can run on GitHub

Status: Proposed   Date: 2026-10-16
Work items: #1659   Classification: Public

The maintainer accepts this record. State on 2026-10-28: the publish and the public CI have run on GitHub; Release Please has not
opened a release pull request yet (the repository setting that lets Actions open pull requests is still off), so the counting of
commits after a sync-back is not proved.

## Context

- The private repository is the source of truth. the publish script (kept in the private repository) filters it (private paths
  removed) and pushes the result to `pluries/craticula`, never by force (one-way, read-only downstream).
- Release Please (TRD OS-2) works only on a GitHub repository. Its release pull request is merged on GitHub, so the
  public side gains a commit that private history does not have.
- After filtering, the next publish is no longer a fast-forward of that public commit: the filtered history does not
  contain it. A force push is ruled out.
- Release Please reads conventional commit subjects. Private subjects are `FEATURE|FIX|TECHNICAL #<id> text`, so the
  mirror maps them after the already published boundary (no public hash changes).
- Release Please walks history by commit date and stops at the last release commit. A merge commit made at publish time
  is dated after the release, and the private commits it brings in may be dated before it.

## Decision

1. The release files of the public release commit (the `<Version>` value, `CHANGELOG.md`, the manifest) return to private
   `main` through the sync script, one private commit with a `Public-Sync: <full public sha>` trailer. The
   trailer is the record of "absorbed"; no file comparison decides it.
2. When the public branch holds a commit the filtered history lacks (the release commit), the publish pushes a merge commit:
   parents the public tip and the filtered head, tree the filtered head's. It is a fast-forward of the public branch, never
   forced. Without such a commit the publish is a plain fast-forward of the filtered head.
3. The merge commit's subject is `chore: ...` (it counts for nothing). Its body lists the mapped subject of every commit
   it brings in as one paragraph each, strongest type first, capped at 200 with a count line. Release Please (v17.3.0
   `splitMessages`) splits a message at each blank line followed by `type: ` and counts every entry, so commits dated
   before the release are counted. Unverified on GitHub.
4. The public repository merges pull requests by squash only and requires linear history, so the release pull request is one
   single-parent commit.

## Alternatives

- **A private equivalent of Release Please, no public merge.** Run a release tool on the private repository. Rejected for
  now: Release Please is named in OS-2 and exists only for GitHub; a private clone of its logic is code to own.
- **Re-create the public repository from the filtered history after each release.** Rewrites public hashes, breaks forks,
  clones and the tags. Rejected.
- **Two-way mirror (GitHub becomes a second source).** Breaks the one-way rule, doubles the review gate and the secret
  scan, and makes the private history depend on public edits. Rejected; the sync-back copies release files only.
- **Make the merge subject the strongest type instead of listing the commits.** Counts once, loses the changelog lines.
  Kept as the fallback if the body listing is not counted.

## Consequences

- Positive: no force push; no public hash changes; a release becomes a pull request merge plus one sync.
- Negative: the public history holds merge commits and is no longer a plain filtered copy; the maintainer runs a sync after each
  release.
- Follow-ups: verify the counting on the first release after a sync-back; record the result in this record.

## Compliance

- No new secret: the default `GITHUB_TOKEN` only. New external action `googleapis/release-please-action` (pinned by SHA); the maintainer
  approves it.
- Sync-back hardening: only the `<Version>` value of the public props file is copied (the file is part of the build, so a
  whole copy would let a public commit change the build); the two plain-text files are copied whole, refused if they are
  not regular files or hold a NUL byte; any other public path is refused.
- No personal data.
