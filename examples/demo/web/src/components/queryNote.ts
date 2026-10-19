/** The dialect the EF Core provider reports; its query text is EF's own `ToQueryString()`. */
export const EF_DIALECT = 'ef-npgsql';

/** The dialect the OpenSearch provider reports; its query text is the compiled search body as JSON. */
export const OPENSEARCH_DIALECT = 'opensearch';

/** A short explanation shown above the query text for dialects whose text reads differently, else null. */
export function queryNote(dialect: string): string | null {
  if (dialect === EF_DIALECT) {
    return 'EF Core inlines parameter values in this text, and grouped requests are composed inside the adapter, so the text may be a one-line comment.';
  }
  if (dialect === OPENSEARCH_DIALECT) {
    return 'This is the compiled search request (JSON, values inside the body). The adapter adds a point in time, search_after and a count request on top of it, so these are not byte for byte the requests it sends.';
  }
  return null;
}
