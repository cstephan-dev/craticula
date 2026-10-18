/** The dialect the EF Core provider reports; its query text is EF's own `ToQueryString()`. */
export const EF_DIALECT = 'ef-npgsql';

/** A short explanation shown above the query text for dialects whose text reads differently, else null. */
export function queryNote(dialect: string): string | null {
  return dialect === EF_DIALECT
    ? 'EF Core inlines parameter values in this text, and grouped requests are composed inside the adapter, so the text may be a one-line comment.'
    : null;
}
