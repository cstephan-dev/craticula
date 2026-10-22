import { describe, expect, it } from 'vitest';
import { queryNote } from './queryNote';

describe('queryNote', () => {
  it.each(['ef-npgsql', 'ef-sqlite', 'ef-sqlserver', 'ef-mysql'])('explains the EF Core text for %s', (dialect) => {
    expect(queryNote(dialect)).toMatch(/EF Core/);
  });

  it('stays silent for the PostgreSQL dialect', () => {
    expect(queryNote('postgres')).toBeNull();
  });

  it.each(['opensearch', 'elasticsearch'])('says the %s text is the compiled request, not exactly what is sent', (dialect) => {
    expect(queryNote(dialect)).toMatch(/compiled search request/);
    expect(queryNote(dialect)).toMatch(/not byte for byte/);
  });

  it('stays silent for MongoDB', () => {
    expect(queryNote('mongo')).toBeNull();
  });
});
