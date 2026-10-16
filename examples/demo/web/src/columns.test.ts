import { describe, expect, it, vi } from 'vitest';
import type { ColDef } from 'ag-grid-enterprise';
import schema from '../../orders.schema.json';
import { buildColumnDefs } from './columns';

const columns = buildColumnDefs({ onError: vi.fn() }) as ColDef[];
const byField = new Map(columns.map((c) => [c.field, c]));

describe('column definitions', () => {
  it('has one column per schema field', () => {
    expect([...byField.keys()].sort()).toEqual(Object.keys(schema.fields).sort());
  });

  it('gives the grouping columns a values callback and row grouping', () => {
    for (const id of ['country', 'category', 'status']) {
      const column = byField.get(id);
      expect(column?.filter).toBe('agSetColumnFilter');
      expect(column?.enableRowGroup).toBe(true);
      expect(typeof (column?.filterParams as { values: unknown }).values).toBe('function');
    }
  });

  it('allows exactly the aggregations the schema allows on amount and quantity', () => {
    for (const id of ['amount', 'quantity'] as const) {
      expect(byField.get(id)?.allowedAggFuncs).toEqual(schema.fields[id].aggregatable);
      expect(byField.get(id)?.enableValue).toBe(true);
    }
  });

  it('keeps unsupported operators (inRange) out of the number and date filters', () => {
    for (const id of ['id', 'amount', 'quantity', 'orderedAt', 'shippedOn']) {
      const options = (byField.get(id)?.filterParams as { filterOptions: string[] }).filterOptions;
      expect(options).not.toContain('inRange');
    }
  });

  it('filters the boolean column as a set of "true" / "false"', () => {
    expect(byField.get('isPriority')?.filterParams).toEqual({ values: ['true', 'false'] });
  });
});

describe('set filter values callback', () => {
  it('loads the values of the active provider and drops nulls', async () => {
    const fetchMock = vi.fn(async () => Response.json({ values: ['DE', 'FR', null] }));
    vi.stubGlobal('fetch', fetchMock);
    const callback = (byField.get('country')?.filterParams as {
      values: (p: unknown) => void;
    }).values;

    const loaded = await new Promise<string[]>((success) => callback({ context: { provider: 'mongo' }, success }));

    expect(loaded).toEqual(['DE', 'FR']);
    expect((fetchMock.mock.calls[0] as unknown[])[0]).toBe('/api/mongo/values');
    vi.unstubAllGlobals();
  });
});
