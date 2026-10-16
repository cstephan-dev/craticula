import { describe, expect, it } from 'vitest';
import ordersSchema from '../../orders.schema.json?raw';
import { ApiError } from './api';
import { FIELD_TYPES, translateInRange } from './inRange';

const both = { filterType: 'number', type: 'inRange', filter: 100, filterTo: 500 };

describe('translateInRange: a number column', () => {
  it('turns both bounds into an exclusive range and removes the column from the filter model', () => {
    expect(translateInRange({ quantity: both })).toEqual({
      found: true,
      filterModel: {},
      customFilters: {
        type: 'comparison',
        field: 'quantity',
        op: 'inRange',
        from: { type: 'int64', value: 100 },
        to: { type: 'int64', value: 500 },
        fromInclusive: false,
        toInclusive: false,
      },
    });
  });

  it('turns a range with only the lower bound into gt', () => {
    expect(translateInRange({ quantity: { ...both, filterTo: null } }).customFilters).toEqual({
      type: 'comparison',
      field: 'quantity',
      op: 'gt',
      value: { type: 'int64', value: 100 },
    });
  });

  it('turns a range with only the upper bound into lt', () => {
    expect(translateInRange({ quantity: { ...both, filter: undefined } }).customFilters).toEqual({
      type: 'comparison',
      field: 'quantity',
      op: 'lt',
      value: { type: 'int64', value: 500 },
    });
  });

  it('drops a range with no bound at all', () => {
    expect(translateInRange({ quantity: { ...both, filter: null, filterTo: null } })).toEqual({ found: true, filterModel: {} });
  });

  it('keeps a zero bound: zero is a value', () => {
    expect(translateInRange({ quantity: { ...both, filter: 0 } }).customFilters).toMatchObject({ from: { type: 'int64', value: 0 } });
  });

  it('writes a decimal column bound as the schema wants it, a plain decimal string', () => {
    const node = translateInRange({ amount: { ...both, filter: 12.5, filterTo: 100 } }).customFilters;
    expect(node).toMatchObject({ from: { type: 'decimal', value: '12.5' }, to: { type: 'decimal', value: '100' } });
  });

  it('reports that a range was found even when it is empty and produces no node', () => {
    const empty = translateInRange({ quantity: { ...both, filter: null, filterTo: null } });
    expect(empty.found).toBe(true);
    expect(translateInRange({ customer: { filterType: 'text', type: 'contains', filter: 'x' } }).found).toBe(false);
  });

  it('refuses a fraction on a whole-number column and an exponent form on a decimal column', () => {
    expect(() => translateInRange({ quantity: { ...both, filter: 1.5 } })).toThrow(ApiError);
    expect(() => translateInRange({ amount: { ...both, filter: 1e-7 } })).toThrow(/plain decimal/);
  });
});

describe('translateInRange: a date column', () => {
  const date = (from: string | null, to: string | null) => ({ filterType: 'date', type: 'inRange', dateFrom: from, dateTo: to });

  it('uses the calendar date for a date column, whatever time the filter adds', () => {
    expect(translateInRange({ shippedOn: date('2025-01-01 00:00:00', '2025-02-01') }).customFilters).toMatchObject({
      op: 'inRange',
      from: { type: 'date', value: '2025-01-01' },
      to: { type: 'date', value: '2025-02-01' },
      fromInclusive: false,
      toInclusive: false,
    });
  });

  it('reads a date-time column bound as UTC, with the time when there is one', () => {
    const node = translateInRange({ orderedAt: date('2025-01-01 00:00:00', '2025-02-01 06:30') }).customFilters;
    expect(node).toMatchObject({
      from: { type: 'datetime', value: '2025-01-01T00:00:00Z', zone: 'UTC' },
      to: { type: 'datetime', value: '2025-02-01T06:30:00Z', zone: 'UTC' },
    });
  });

  it('handles one bound on a date column', () => {
    expect(translateInRange({ shippedOn: date(null, '2025-03-01') }).customFilters).toEqual({
      type: 'comparison',
      field: 'shippedOn',
      op: 'lt',
      value: { type: 'date', value: '2025-03-01' },
    });
  });

  it('refuses a time other than midnight on a date column instead of cutting it off', () => {
    expect(() => translateInRange({ shippedOn: date('2025-01-01 05:00:00', '2025-02-01') })).toThrow(/midnight/);
  });

  it('refuses a bound that is not a date', () => {
    expect(() => translateInRange({ shippedOn: date('soon', '2025-03-01') })).toThrow(/YYYY-MM-DD/);
  });
});

describe('translateInRange: other models', () => {
  it('leaves a model without a range exactly as it is (same object)', () => {
    const model = { customer: { filterType: 'text', type: 'contains', filter: 'GmbH' }, amount: { filterType: 'number', type: 'greaterThan', filter: 5 } };
    const result = translateInRange(model);
    expect(result.filterModel).toEqual(model);
    expect(result.filterModel.customer).toBe(model.customer);
    expect(result.customFilters).toBeUndefined();
  });

  it('never changes its input and accepts null', () => {
    const model = { quantity: both };
    translateInRange(model);
    expect(model).toEqual({ quantity: both });
    expect(translateInRange(null)).toEqual({ found: false, filterModel: {} });
  });

  it('keeps the other conditions of an AND column in the filter model and moves only the range', () => {
    const other = { filterType: 'number', type: 'notEqual', filter: 7 };
    const result = translateInRange({ quantity: { filterType: 'number', operator: 'AND', conditions: [other, both] } });
    expect(result.filterModel).toEqual({ quantity: other });
    expect(result.customFilters).toMatchObject({ op: 'inRange', field: 'quantity' });
  });

  it('keeps several other conditions joined by AND, and handles the legacy condition1/condition2 form', () => {
    const a = { filterType: 'number', type: 'notEqual', filter: 7 };
    const b = { filterType: 'number', type: 'lessThan', filter: 900 };
    const result = translateInRange({ quantity: { filterType: 'number', operator: 'AND', conditions: [a, both, b] } });
    expect(result.filterModel).toEqual({ quantity: { filterType: 'number', operator: 'AND', conditions: [a, b] } });
    const legacy = translateInRange({ quantity: { filterType: 'number', operator: 'AND', condition1: a, condition2: both } });
    expect(legacy.filterModel).toEqual({ quantity: a });
  });

  it('joins ranges of several columns with and', () => {
    const result = translateInRange({ quantity: both, amount: { ...both, filter: 1, filterTo: 2 } });
    expect(result.customFilters).toMatchObject({ type: 'and' });
    expect((result.customFilters as { nodes: unknown[] }).nodes).toHaveLength(2);
  });

  it('refuses a range joined to another condition by OR, with a clear error', () => {
    const other = { filterType: 'number', type: 'equals', filter: 7 };
    const attempt = () => translateInRange({ quantity: { filterType: 'number', operator: 'OR', conditions: [other, both] } });
    expect(attempt).toThrow(ApiError);
    expect(attempt).toThrow(/cannot be combined with another condition by OR/);
    try {
      attempt();
    } catch (error) {
      expect(error).toMatchObject({ status: 400, kind: 'unsupported-filter' });
    }
  });

  it('refuses a range on a column that has no numeric or date type', () => {
    expect(() => translateInRange({ customer: both })).toThrow(/not offered/);
  });
});

describe('FIELD_TYPES', () => {
  it('equals the field types of orders.schema.json, so it cannot drift', () => {
    const schema = JSON.parse(ordersSchema) as { fields: Record<string, { type: string }> };
    expect(FIELD_TYPES).toEqual(Object.fromEntries(Object.entries(schema.fields).map(([name, field]) => [name, field.type])));
  });
});
