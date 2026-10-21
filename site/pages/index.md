# Craticula

Craticula turns AG Grid server-side requests into native, injection-safe SQL, MongoDB and OpenSearch queries. It compiles the
query and never runs it; you run it with your own driver.

This site is built from the repository's own documents, so it cannot drift from them.

- [Quickstart: C#](quickstart-csharp.html): from an AG Grid request to parameterized SQL in one small program. TypeScript, Python and Go are [planned](quickstart-typescript.html) for after the spec freeze.
- [Index and database guidance](index-guidance.html): what each database needs from you.
- [Overview](../../README.md): what the library is, what it covers and what it leaves out.
- [The spec](../../spec/README.md): the language-neutral source of truth every implementation conforms to.
- [Semantics](../../spec/semantics.md): the normative rules. Every rule has an anchor of its own, for example `semantics.html#grp-7`.
- [Rule index](rules.html): every numbered rule with a link, to look one up by its id.
- [GridSchema reference](grid-schema.html) and [IR reference](ir-schema.html): every member of the two JSON Schemas, with its type and description.

- [Migrating from OData](odata-migration.html): what you wrote in `$filter`, `$orderby`, `$top`/`$skip` and `$apply`, and the request Craticula takes instead.
