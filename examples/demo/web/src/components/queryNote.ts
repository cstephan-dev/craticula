/** The dialects the EF Core providers report (`ef-npgsql`, `ef-sqlite`, `ef-sqlserver`); their query text is EF's own `ToQueryString()`. */
export const isEfDialect = (dialect: string): boolean => dialect.startsWith('ef-');

/** The dialects the search providers report (`opensearch`, `elasticsearch`); their query text is the compiled search body as JSON. */
export const isSearchDialect = (dialect: string): boolean => dialect === 'opensearch' || dialect === 'elasticsearch';

/** A short explanation shown above the query text for dialects whose text reads differently, else null. */
export function queryNote(dialect: string): string | null {
  if (isEfDialect(dialect)) {
    return 'EF Core inlines parameter values in this text, and grouped requests are composed inside the adapter, so the text may be a one-line comment.';
  }
  if (isSearchDialect(dialect)) {
    return 'This is the compiled search request (JSON, values inside the body). The adapter adds a point in time, search_after and a count request on top of it, so these are not byte for byte the requests it sends.';
  }
  return null;
}
