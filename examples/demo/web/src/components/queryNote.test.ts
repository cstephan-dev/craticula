import { describe, expect, it } from 'vitest';
import { queryNote } from './queryNote';

describe('queryNote', () => {
  it('explains the EF Core text', () => {
    expect(queryNote('ef-npgsql')).toMatch(/EF Core/);
  });

  it('stays silent for the PostgreSQL dialect', () => {
    expect(queryNote('postgres')).toBeNull();
  });

  it('says the OpenSearch text is the compiled request, not exactly what is sent', () => {
    expect(queryNote('opensearch')).toMatch(/compiled search request/);
    expect(queryNote('opensearch')).toMatch(/not byte for byte/);
  });

  it('stays silent for MongoDB', () => {
    expect(queryNote('mongo')).toBeNull();
  });
});
