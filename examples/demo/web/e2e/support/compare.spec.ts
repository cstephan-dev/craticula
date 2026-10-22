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

// Each rule below is pinned on both sides of its scope: what it excuses passes, one step outside it fails.
const one = (key: string, value: number) => ({ lastRow: 1, rows: [{ country: 'Austria', [key]: value }] });

test('the null-group rule excuses only a move of the null group, not other differences', () => {
  const groups = { lastRow: 3, rows: [{ country: 'A', n: 1 }, { country: 'B', n: 2 }, { country: null, n: 3 }] };
  const check = (provider: 'opensearch' | 'elasticsearch', rows: Array<Record<string, unknown>>) =>
    compareRows({ scenario: 'group', provider, baseline: groups, actual: { lastRow: 3, rows } }).ok;
  for (const provider of ['opensearch', 'elasticsearch'] as const) {
    // A different value in a keyed group, next to the moved null group.
    expect(check(provider, [{ country: null, n: 3 }, { country: 'A', n: 1 }, { country: 'B', n: 99 }])).toBe(false);
    // The keyed groups in another order.
    expect(check(provider, [{ country: null, n: 3 }, { country: 'B', n: 2 }, { country: 'A', n: 1 }])).toBe(false);
    // The null group itself carries another value.
    expect(check(provider, [{ country: null, n: 4 }, { country: 'A', n: 1 }, { country: 'B', n: 2 }])).toBe(false);
    // The null group in the middle is not "the other end".
    expect(check(provider, [{ country: 'A', n: 1 }, { country: null, n: 3 }, { country: 'B', n: 2 }])).toBe(false);
    // The null group missing altogether.
    expect(check(provider, [{ country: 'A', n: 1 }, { country: 'B', n: 2 }, { country: 'C', n: 3 }])).toBe(false);
    // The null group where it already was: equal, so no divergence is needed.
    expect(compareRows({ scenario: 'group', provider, baseline: groups, actual: groups })).toEqual({ ok: true, message: '', applied: [] });
  }
});

test('an unknown lastRow is excused only above the cap, only on a search engine, only as null', () => {
  const at = (lastRow: number) => ({ lastRow, rows: [{ id: 1 }] });
  const unknown = { lastRow: null, rows: [{ id: 1 }] };
  for (const provider of ['opensearch', 'elasticsearch'] as const) {
    expect(compareRows({ scenario: 'scroll', provider, baseline: at(10_001), actual: unknown }).ok).toBe(true);
    expect(compareRows({ scenario: 'scroll', provider, baseline: at(10_000), actual: unknown }).ok).toBe(false);
    expect(compareRows({ scenario: 'scroll', provider, baseline: at(5_000), actual: unknown }).ok).toBe(false);
    expect(compareRows({ scenario: 'scroll', provider, baseline: at(20_000), actual: at(15_000) }).ok).toBe(false);
  }
});

test('the SQLite sum tolerance is relative 1e-9 and covers only SQLite sums', () => {
  const base = one('amount_sum', 10);
  const off = (provider: 'sqlite' | 'mongo' | 'sqlserver', delta: number) =>
    compareRows({ scenario: 'sums', provider, baseline: base, actual: one('amount_sum', 10 + delta) }).ok;
  expect(off('sqlite', 1e-10)).toBe(true);
  expect(off('sqlite', 1e-6)).toBe(false);
  // Any other provider sums exactly.
  expect(off('mongo', 1e-10)).toBe(false);
  expect(off('mongo', 1e-7)).toBe(false);
  expect(off('sqlserver', 1e-10)).toBe(false);
  // Only keys ending in _sum: another SQLite key stays exact.
  expect(compareRows({ scenario: 'sums', provider: 'sqlite', baseline: one('amount_max', 10), actual: one('amount_max', 10 + 1e-10) }).ok).toBe(false);
});

test('the average tolerance (relative 1e-6) covers every provider but only keys ending in _avg', () => {
  for (const provider of ['mongo', 'postgres', 'sqlite'] as const) {
    expect(compareRows({ scenario: 'avg', provider, baseline: one('amount_avg', 10), actual: one('amount_avg', 10 + 1e-7) }).ok).toBe(true);
    expect(compareRows({ scenario: 'avg', provider, baseline: one('amount_avg', 10), actual: one('amount_avg', 10 + 1e-4) }).ok).toBe(false);
    // A key that does not end in _avg stays exact.
    expect(compareRows({ scenario: 'avg', provider, baseline: one('amount_min', 10), actual: one('amount_min', 10 + 1e-7) }).ok).toBe(false);
    expect(compareRows({ scenario: 'avg', provider, baseline: one('average_note', 10), actual: one('average_note', 10 + 1e-7) }).ok).toBe(false);
  }
});

test('the MySQL average is PostgreSQL\'s value rounded half up to four decimals, and nothing else', () => {
  const check = (actual: number) =>
    compareRows({ scenario: 'avg', provider: 'mysql', baseline: one('quantity_avg', 25.349264), actual: one('quantity_avg', actual) }).ok;
  expect(check(25.3493)).toBe(true);
  expect(check(25.349264)).toBe(true);
  // Cut to a whole number is SQL Server's behaviour, not MySQL's.
  expect(check(25)).toBe(false);
  expect(check(25.3492)).toBe(false);
  expect(check(25.3494)).toBe(false);
  // The rounding is half up on the fifth decimal.
  expect(compareRows({ scenario: 'avg', provider: 'mysql', baseline: one('quantity_avg', 25.34925), actual: one('quantity_avg', 25.3493) }).ok).toBe(true);
});

test('the SQL Server average is PostgreSQL\'s value cut toward zero, and nothing else', () => {
  const check = (actual: number) =>
    compareRows({ scenario: 'avg', provider: 'sqlserver', baseline: one('quantity_avg', 25.9), actual: one('quantity_avg', actual) }).ok;
  expect(check(25)).toBe(true);
  expect(check(26)).toBe(false);
  expect(check(25.9)).toBe(true);
});

test('every listed rule carries a reason', () => {
  for (const rule of NUMBER_RULES) {
    expect(rule.reason.length).toBeGreaterThan(10);
  }
  expect(divergencesFor('opensearch').map((item) => item.id)).toContain('search-row-count-unknown');
});
