# Versioning

Craticula uses [Semantic Versioning](https://semver.org): `MAJOR.MINOR.PATCH`.

## Two versions

- The **spec version** is written in [`spec/VERSION`](spec/VERSION). It versions the specification: the IR schema,
  the semantics and the conformance fixtures. Each implementation states the spec version it passes.
- The **package version** is the version of the .NET packages. All ten packages share one version number.

The two move separately. The compatibility table in the [README](README.md#compatibility) shows which spec version
the latest release implements.

## What drives the package version

- The package version follows the public API of the packages and the spec version they implement.
- It never follows the version of a dependency. A new major of AG Grid, a database driver or any other library does
  not change our version by itself.

## What is a breaking change

A change is breaking, and needs a new major version, when it does any of these:

- It removes or changes a public type or member, so that code that compiled before no longer compiles.
- It changes the compiled artifact (the SQL text and parameters, the MongoDB pipeline or the search query) for the
  same request and schema.
- It changes an error kind: a request that was accepted is rejected, or the other way round, or a rejection now has a
  different kind.

A spec change that changes artifacts is at least a minor version of the packages. It is a major version when it is
also breaking by the rules above.

## Adapters and provider majors

Each adapter package declares a version range for the library it wraps: the current major, with the lowest version
we have tested as the floor. It never pins one exact version.

When a provider ships a new major:

- Widen the range to include it.
- If our public API survives unchanged, bump the minor version.
- If our public API has to change, bump the major version.
- Publish parallel package lines only when both provider majors must be supported at the same time.

## Tags and commits

- A release is tagged `dotnet-vX.Y.Z`.
- Commit types decide the bump: `feat` is a minor, `fix` is a patch. A `!` after the type, or a `BREAKING CHANGE:`
  footer, is a major. Other types do not open a release.

## The conformance fixture suite

The fixture suite is versioned by the spec version. The release notes also show how many fixtures the suite holds.
That number is a count, not a version.
