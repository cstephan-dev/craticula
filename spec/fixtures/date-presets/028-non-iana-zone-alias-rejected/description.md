Proves that a `requestContext.zone` naming a tzdata *artifact* rather than a real zone is rejected — the one zone case a shape check cannot catch, and the one with a genuine security consequence.

The request is byte-identical to `001-today` apart from its zone, which is `localtime`. On a typical Linux host with .NET, `localtime`:

1. **resolves successfully** — `TimeZoneInfo.FindSystemTimeZoneById("localtime")` returns a zone rather than throwing, so the "did it resolve?" guard `027` exercises does not fire here; and
2. **satisfies `ir.schema.json`'s IANA-zone-id shape** — it is lowercase letters with no separator, so the structural pattern accepts it and it would travel downstream as a well-formed-looking `datetimeValue.zone` provenance value; and
3. **means "whatever zone this server process is configured for"** — it is a pointer to the host's own configuration, not a zone in its own right.

Together those make it the worst of the three: a client that sends `zone: "localtime"` silently relocates every preset boundary into the *server's* zone, and nothing downstream can tell. That is exactly what SEM-4's "must NOT assume a fixed server or database default timezone anywhere in the compiler; zone resolution happens once, in the adapter, using the request context's zone" exists to prevent, arrived at from the opposite direction — the adapter *did* use the request's zone, but the request named the server's.

`Factory` (tzdata's deliberately-unset placeholder) and `posixrules` (a legacy POSIX-TZ artifact) resolve and pass the shape check the same way and must be rejected identically; `localtime` is fixtured here as the representative case because it is the one that leaks the host's configuration into the result.

Like `027`, this is a pure wire-shape property with a host-independent outcome — every port can replay it and must reach `requestParsing` / `malformed-request`.
