import { expect, test } from '@playwright/test';
import { compareRows, expectSameRows } from './compare';
import { divergencesFor, NUMBER_RULES } from './divergences';

// These checks need neither a browser nor the demo: they prove that the row comparison passes equal rows, names the provider
// and the scenario on a difference, and reports the reason of an accepted divergence.
const baseline = {
  lastRow: 3,
  rows: [
    { id: 1, customer: 'Acme', amount: 10.5 },
    { id: 2, customer: 'Globex', amount: 20 },
    { id: 3, customer: 'Initech', amount: 30.25 },
  ],
};

const rows = (...edits: Array<(row: Record<string, unknown>) => unknown>) =>
  baseline.rows.map((row, index) => {
    const copy = { ...row };
    edits[index]?.(copy);
    return copy;
  });

test('equal rows pass and need no divergence', () => {
  const result = compareRows({ scenario: 'first page', provider: 'mongo', baseline, actual: { lastRow: 3, rows: rows() } });
  expect(result).toEqual({ ok: true, message: '', applied: [] });
  expectSameRows({ scenario: 'first page', provider: 'mongo', baseline, actual: { lastRow: 3, rows: rows() } });
});

test('a deliberate difference fails and names provider, scenario, row and column', () => {
  const actual = { lastRow: 3, rows: rows(() => undefined, (row) => (row.customer = 'Globex Ltd')) };
  const result = compareRows({ scenario: 'sort by amount', provider: 'mongo', baseline, actual });
  expect(result.ok).toBe(false);
  expect(result.message).toContain('mongo');
  expect(result.message).toContain('sort by amount');
  expect(result.message).toContain('postgres');
  expect(result.message).toContain('row 1, column "customer"');
  expect(() => expectSameRows({ scenario: 'sort by amount', provider: 'mongo', baseline, actual })).toThrow(/mongo.*sort by amount|sort by amount.*mongo/);
});

test('a different row count and a different lastRow fail with a reason', () => {
  const shorter = compareRows({ scenario: 'paging', provider: 'sqlite', baseline, actual: { lastRow: 3, rows: rows().slice(0, 2) } });
  expect(shorter.message).toContain('3 rows expected but got 2');
  const total = compareRows({ scenario: 'paging', provider: 'sqlite', baseline, actual: { lastRow: 2, rows: rows() } });
  expect(total.message).toContain('lastRow expected 3 but got 2');
});

test('an accepted divergence passes and shows its reason', () => {
  // SQL Server's integer average arrives truncated: 25.4 as 25.
  const grouped = { lastRow: 1, rows: [{ country: 'Austria', quantity_avg: 25.4181 }] };
  const actual = { lastRow: 1, rows: [{ country: 'Austria', quantity_avg: 25 }] };
  const result = compareRows({ scenario: 'group by country', provider: 'sqlserver', baseline: grouped, actual });
  expect(result.ok).toBe(true);
  expect(result.applied.map((item) => item.id)).toEqual(['sqlserver-integer-average']);
  expect(result.applied[0].reason).toContain('integer');
  // The same difference on a provider with no such rule is a failure.
  expect(compareRows({ scenario: 'group by country', provider: 'mongo', baseline: grouped, actual }).ok).toBe(false);
});

test('a listed divergence does not excuse a larger difference', () => {
  const grouped = { lastRow: 1, rows: [{ country: 'Austria', amount_sum: 1000.5 }] };
  const near = { lastRow: 1, rows: [{ country: 'Austria', amount_sum: 1000.5 + 1e-8 }] };
  const far = { lastRow: 1, rows: [{ country: 'Austria', amount_sum: 1000.6 }] };
  expect(compareRows({ scenario: 'sums', provider: 'sqlite', baseline: grouped, actual: near }).applied.map((item) => item.id)).toEqual(['sqlite-sums']);
  expect(compareRows({ scenario: 'sums', provider: 'sqlite', baseline: grouped, actual: far }).ok).toBe(false);
});

test('search engines may report an unknown count above the cap and move the null group', () => {
  const big = { lastRow: 20_000, rows: [{ id: 1 }] };
  const unknown = compareRows({ scenario: 'scroll', provider: 'opensearch', baseline: big, actual: { lastRow: null, rows: [{ id: 1 }] } });
  expect(unknown.ok).toBe(true);
  expect(unknown.applied[0].reason).toContain('does not count');
  expect(compareRows({ scenario: 'scroll', provider: 'mongo', baseline: big, actual: { lastRow: null, rows: [{ id: 1 }] } }).ok).toBe(false);

  const groups = { lastRow: 3, rows: [{ country: 'A', n: 1 }, { country: 'B', n: 2 }, { country: null, n: 3 }] };
  const moved = { lastRow: 3, rows: [{ country: null, n: 3 }, { country: 'A', n: 1 }, { country: 'B', n: 2 }] };
  const result = compareRows({ scenario: 'group', provider: 'elasticsearch', baseline: groups, actual: moved });
  expect(result.ok).toBe(true);
  expect(result.applied.map((item) => item.id)).toContain('search-null-group-at-other-end');
  expect(compareRows({ scenario: 'group', provider: 'postgres', baseline: groups, actual: moved }).ok).toBe(false);
});

test('every listed rule carries a reason', () => {
  for (const rule of NUMBER_RULES) {
    expect(rule.reason.length).toBeGreaterThan(10);
  }
  expect(divergencesFor('opensearch').map((item) => item.id)).toContain('search-row-count-unknown');
});
