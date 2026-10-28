# Contributing to Craticula

Thank you for helping. This page tells you what we need from a pull request and how to check it yourself before you open it.

## Before you start

- **A bug or a small fix:** open a pull request directly.
- **A new feature or a change of behavior:** open an issue first, so we can agree on the shape before you write code.
- **A security problem:** do not open an issue or a pull request. Follow [SECURITY.md](SECURITY.md).
- Everyone who takes part follows the [code of conduct](CODE_OF_CONDUCT.md).

## Sign every commit (DCO)

We use the [Developer Certificate of Origin](https://developercertificate.org/) instead of a contributor license agreement. Each
commit must end with a line that says you wrote the change, or have the right to send it, under this project's license
(Apache-2.0):

```
Signed-off-by: Your Name <you@example.com>
```

`git` adds it for you:

```bash
git commit -s -m "fix: handle an empty in list"
```

- **Forgot it on the last commit:** `git commit --amend -s`, then `git push --force-with-lease` on your own branch.
- **Forgot it on several commits:** `git rebase --signoff origin/main`, then push the same way.
- The e-mail in the line must be the e-mail of the commit's own author. The `dco` check on your pull request fails if a commit
  has no line, and it names that commit and how to fix it.

## Commit subjects and pull request titles

Use [conventional commits](https://www.conventionalcommits.org/): `feat:`, `fix:`, `docs:`, `test:`, `refactor:`, `chore:`. A breaking change
is `feat!:` or a `BREAKING CHANGE:` line in the body. We merge by squash, and the squash takes your pull request title, so
write the title as you would write the commit subject. A maintainer may edit it when the pull request is merged.

## Build and test

You need the .NET 10 SDK and Python 3.10 or newer (the conformance tests run a Python runner). From the repository root:

```bash
dotnet build csharp/Craticula.sln
dotnet test csharp/Craticula.sln --filter "FullyQualifiedName!~MongoDB.Driver.Tests&FullyQualifiedName!~OpenSearch.Client.Tests&FullyQualifiedName!~Integration.Tests.Containers"
```

That is the offline suite: no database is needed. The build must end with `0 Warning(s)`.

- **Coverage gate:** `bash scripts/coverage.sh` runs the same tests with coverage and fails when a published assembly is below 90% lines
  (about five minutes).
- **Licenses:** a new NuGet package must carry a license from the allowlist. The README, section "Dependency licenses",
  says how to run `python3 scripts/check-licenses.py`; the `license-check` job runs it on every pull request.
- **Against real databases (optional):** `Craticula.Integration.Tests` starts PostgreSQL, MySQL, SQL Server, Oracle, MongoDB, OpenSearch
  and Elasticsearch in Docker. Run it with Docker available:
  `CRATICULA_INTEGRATION_DOCKER=on dotnet test csharp/tests/Craticula.Integration.Tests`. The pull request checks do not need it.

## The spec comes first

`spec/` (the JSON Schemas, `semantics.md` and the fixtures) is the source of truth, and the C# code follows it. A change of behavior
lands in the spec first: the schema or the rule, then a fixture that shows it, then the code. The spec is never edited to match
what the code happens to do.

## What happens to your pull request

A maintainer reviews it, and the checks `build`, `test`, `license-check` and `dco` must pass. When it is merged, the
maintainers copy the change into their own repository and keep you as its author with your sign-off. Nothing else is asked of you.
