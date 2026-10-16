# Craticula

> A compiler, not a client: translate AG Grid Server-Side Row Model requests into native,
> secure backend queries — SQL, MongoDB, OpenSearch/Elasticsearch — from one canonical IR,
> across four languages.

**Status:** early design — see [`docs/TRD-grid-query-compiler.md`](docs/TRD-grid-query-compiler.md)
for the full technical requirements document (architecture, semantics spec, security model,
roadmap, and open questions).

## Why

Every backend team re-implements the translation of an AG Grid SSRM request
(`filterModel`, `sortModel`, `rowGroupCols`, `groupKeys`, `valueCols`, `startRow`/`endRow`)
into database queries — usually with string DSL intermediaries, missing operators, broken
null/case semantics, injection surface, and no grouping or search-engine support. No
maintained open-source library covers this end to end.

## How it works

```
AG Grid SSRM request ──▶ Frontend adapter ──▶ Canonical IR ──▶ Backend compiler ──▶ Query artifact
                                                                                    ├─ { sql, params[] }
                                                                                    ├─ Mongo aggregation pipeline
                                                                                    └─ OpenSearch/ES query DSL
```

The core never opens a connection: compilation is a pure function from a grid request plus
a schema configuration (the security and typing boundary) to a plain query artifact.
Execution — running the artifact and shaping results back into the grid response — lives in
separate, optional per-ecosystem adapter packages.

## Scope (v1)

- **Frontend adapter:** AG Grid SSRM (v31–v36).
- **Backends:** PostgreSQL, SQL Server, MySQL/MariaDB, SQLite, MongoDB, OpenSearch,
  Elasticsearch. Oracle is open (see the TRD's open questions).
- **Reference implementation:** C#/.NET, ported to TypeScript, Python, and Go after the spec
  freezes at 1.0.
- **Semantics** (case sensitivity, null handling/ordering, dates & zones, deterministic
  paging) are normative and enforced by a shared, language-agnostic conformance fixture suite.
- CSV/TSV streaming export shares the same compile-only input model.

See [§2.2 Non-goals](docs/TRD-grid-query-compiler.md#22-non-goals) and
[§14 Roadmap](docs/TRD-grid-query-compiler.md#14-roadmap-post-10) for what's explicitly out of
scope for v1.

## Repository layout (planned)

```
spec/         IR JSON Schema, semantics.md, conformance fixtures — the source of truth
csharp/       reference implementation (Core, AgGrid adapter, compilers, execution adapters)
typescript/   TypeScript port (after spec freeze)
python/       Python port (after spec freeze)
go/           Go port (after spec freeze)
docs/         requirements, architecture, ADRs
examples/     demo app (React + AG Grid + one sample backend)
```

## Packaging

The C# implementation is split into ten NuGet packages: `Craticula.Core`, `Craticula.AgGrid`, `Craticula.Sql`,
`Craticula.Mongo`, `Craticula.Search`, `Craticula.Export.Csv`, `Craticula.EntityFrameworkCore`, `Craticula.Dapper`,
`Craticula.MongoDB.Driver` and `Craticula.OpenSearch.Client`. Nothing is published to nuget.org until the release routine has run; the `Latest release:` line in the
[Compatibility](#compatibility) section shows the last published version. To check that the packages build and a clean consumer compiles against them, run
`bash scripts/pack-smoke.sh` (Python 3.10+ and the .NET 10 SDK; about 3 minutes).

## Demo

[`examples/demo`](examples/demo/README.md) is a small app: one AG Grid page that reads the same orders from PostgreSQL or
MongoDB through a C# API built on the library. It needs Docker, the .NET 10 SDK and Node 20.19 or newer. It is a showcase
that references the library by project reference, not a published package.

## Compatibility

| Spec version | .NET target |
|---|---|
| 0.1.0 | net10.0 |

The spec version is the one in [`spec/VERSION`](spec/VERSION); every package states it in its own README.
How versions are chosen is written in [VERSIONING.md](VERSIONING.md).
The packages declare version ranges for the libraries they wrap, never one exact version.

Latest release: 1.0.0 <!-- x-release-please-version -->

## License

Apache License 2.0 — see [LICENSE](LICENSE).
