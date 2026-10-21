# Craticula

Craticula turns AG Grid server-side requests into native, injection-safe SQL, MongoDB and OpenSearch queries. It compiles the
query and never runs it; you run it with your own driver.

This site is built from the repository's own documents, so it cannot drift from them.

- [Overview](../../README.md): what the library is, what it covers and what it leaves out.
- [The spec](../../spec/README.md): the language-neutral source of truth every implementation conforms to.
- [Semantics](../../spec/semantics.md): the normative rules. Every rule has an anchor of its own, for example `semantics.html#grp-7`.
- [Rule index](rules.html): every numbered rule with a link, to look one up by its id.
- [GridSchema reference](grid-schema.html) and [IR reference](ir-schema.html): every member of the two JSON Schemas, with its type and description.

More pages (quickstarts, index guidance and a migration guide from OData) follow.
