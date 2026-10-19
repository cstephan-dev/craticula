#!/usr/bin/env bash
# DCO check for a pull request: every commit in <base>..<head> carries a `Signed-off-by: Name <email>` TRAILER whose e-mail is
# the commit's author's or committer's (case-insensitive). A sign-off written in the middle of the message, a malformed one,
# or one by somebody else does not count. (The Developer Certificate of Origin: https://developercertificate.org)
#   scripts/check-dco.sh <base sha> <head sha>
# Exit 0: every commit is signed off (or the check is exempt); 1: at least one is not (each is listed with its short sha and
# subject, and how to fix it); 2: usage problem.
# A merge commit is exempt only when it brings nothing of its own (`git diff-tree --cc` prints no combined diff for it); a
# merge that adds or changes content is checked like any other commit.
# DCO_EXEMPT=<reason> skips the check and says why. Only the workflow sets it, from the pull request AUTHOR'S LOGIN and its
# head repository (which a contributor cannot choose) and only for a pull request from this repository, never from a commit's
# author e-mail (anyone can write any e-mail into a commit). See .github/workflows/dco.yml.
# Text taken from commits is printed without control characters (so a commit cannot fake a workflow command or a line) and
# the list goes into a code fence in the job summary. GITHUB_STEP_SUMMARY, when set, receives the same report.
# Test override: DCO_REPO (the repository to read, default `.`).
set -euo pipefail
die() { echo "check-dco: $*" >&2; exit 2; }
[ $# -eq 2 ] || die "usage: scripts/check-dco.sh <base sha> <head sha>"
BASE="$1"; HEAD="$2"
[[ "$BASE" =~ ^[0-9a-f]{40}$ ]] || die "the base must be a full 40-character commit sha"
[[ "$HEAD" =~ ^[0-9a-f]{40}$ ]] || die "the head must be a full 40-character commit sha"
cd "${DCO_REPO:-.}"
report() {
  printf '%s\n' "$1"
  if [ -n "${GITHUB_STEP_SUMMARY:-}" ]; then printf '%s\n' "$1" >>"$GITHUB_STEP_SUMMARY"; fi
}
if [ -n "${DCO_EXEMPT:-}" ]; then
  report "DCO check skipped: ${DCO_EXEMPT}."
  exit 0
fi
git cat-file -e "$BASE^{commit}" 2>/dev/null || die "the base commit $BASE is not in this clone (fetch the full history)"
git cat-file -e "$HEAD^{commit}" 2>/dev/null || die "the head commit $HEAD is not in this clone (fetch the full history)"
lower() { printf '%s' "$1" | tr '[:upper:]' '[:lower:]'; }
# Text from a commit for the log and the summary: no control character at all, no backtick (it would close the code fence).
safe() { printf '%s' "$1" | tr -d '[:cntrl:]' | tr '`' "'"; }
COMMITS="$(git rev-list "$BASE..$HEAD")"   # an assignment: a failing git ends the script here instead of reading as "no commits"
checked=0; failed=0; merges=0; bad=""
while IFS= read -r commit; do
  [ -n "$commit" ] || continue
  parents="$(git rev-list --parents -n 1 "$commit" | wc -w)"
  if [ "$parents" -gt 2 ] && [ -z "$(git diff-tree --cc --no-commit-id -p "$commit")" ]; then
    merges=$((merges + 1))     # a merge that brings nothing of its own
    continue
  fi
  checked=$((checked + 1))
  author="$(lower "$(git log -1 --format=%ae "$commit")")"
  committer="$(lower "$(git log -1 --format=%ce "$commit")")"
  ok=0
  while IFS= read -r line; do
    email="$(printf '%s' "$line" | sed -nE 's/^.+ <([^<>[:space:]]+@[^<>[:space:]]+)>[[:space:]]*$/\1/p')"
    [ -n "$email" ] || continue
    email="$(lower "$email")"
    if [ "$email" = "$author" ] || [ "$email" = "$committer" ]; then ok=1; fi
  done < <(git log -1 --format='%(trailers:key=Signed-off-by,valueonly)' "$commit")
  if [ "$ok" -eq 0 ]; then
    failed=$((failed + 1))
    bad+="${commit:0:7} $(safe "$(git log -1 --format=%s "$commit")") (author $(safe "$(git log -1 --format=%ae "$commit")"))"$'\n'
  fi
done <<<"$COMMITS"
if [ "$failed" -gt 0 ]; then
  report "DCO check failed: $failed of $checked commit(s) have no 'Signed-off-by: Name <email>' trailer with the author's or committer's e-mail:

\`\`\`
${bad}\`\`\`

Sign off with the e-mail of the commit:
  git commit --amend -s              (the last commit)
  git rebase --signoff $BASE         (every commit of the branch), then git push --force-with-lease
Why: the Developer Certificate of Origin (https://developercertificate.org) is the contributor's own statement that they may submit the change."
  exit 1
fi
report "DCO check passed: $checked commit(s) signed off ($merges merge commit(s) with no content of their own are exempt)."
