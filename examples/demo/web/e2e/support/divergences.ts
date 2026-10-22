import type { Provider } from '../../src/api';

/** One accepted difference between a provider's answer and the baseline's, with the reason it is not a defect. */
export interface Divergence {
  id: string;
  reason: string;
}

export type Row = Record<string, unknown>;

type NumberMode =
  | { kind: 'relative'; tolerance: number }
  /** The provider's value must equal the baseline value with its decimals cut off (toward zero). */
  | { kind: 'truncate' }
  /** The provider's value must equal the baseline value rounded half up to `places` decimals. */
  | { kind: 'round'; places: number };

/** A numeric difference accepted for the keys ending in `keySuffix`, on `providers` (or on every provider). */
export interface NumberRule extends Divergence {
  providers: readonly Provider[] | 'all';
  keySuffix: string;
  mode: NumberMode;
}

export const SEARCH_ENGINES: readonly Provider[] = ['opensearch', 'elasticsearch'];

/**
 * The accepted numeric differences. The first rule that matches a provider and a key decides, so the provider-specific
 * rules come before the rule for every provider. A rule only excuses a pair of numbers that are not already equal.
 */
export const NUMBER_RULES: readonly NumberRule[] = [
  {
    id: 'sqlite-sums',
    providers: ['sqlite'],
    keySuffix: '_sum',
    mode: { kind: 'relative', tolerance: 1e-9 },
    reason: 'SQLite stores NUMERIC as floating point, so a sum can differ from the baseline in its last digits',
  },
  {
    id: 'sqlserver-integer-average',
    providers: ['sqlserver'],
    keySuffix: 'quantity_avg',
    mode: { kind: 'truncate' },
    reason: 'SQL Server\'s AVG over an integer column returns an integer, so 25.418 arrives as 25',
  },
  {
    id: 'mysql-integer-average',
    providers: ['mysql'],
    keySuffix: 'quantity_avg',
    mode: { kind: 'round', places: 4 },
    reason: 'MySQL\'s AVG over an integer column returns a DECIMAL with 4 extra decimals, so 25.349264 arrives as 25.3493',
  },
  {
    id: 'average-tolerance',
    providers: 'all',
    keySuffix: '_avg',
    mode: { kind: 'relative', tolerance: 1e-6 },
    reason: 'an average can differ in the 7th significant digit between engines (decimal versus floating point)',
  },
];

/** Search engines do not count above this many rows. */
export const SEARCH_COUNT_CAP = 10_000;

export interface LastRowRule extends Divergence {
  providers: readonly Provider[];
  above: number;
}

export const LAST_ROW_UNKNOWN: LastRowRule = {
  id: 'search-row-count-unknown',
  providers: SEARCH_ENGINES,
  above: SEARCH_COUNT_CAP,
  reason: `a search engine does not count above ${SEARCH_COUNT_CAP.toLocaleString('en-US')} rows, so lastRow is null where the baseline's is larger; the grid keeps asking until a short block`,
};

export const NULL_GROUP_AT_OTHER_END: Divergence & { providers: readonly Provider[] } = {
  id: 'search-null-group-at-other-end',
  providers: SEARCH_ENGINES,
  reason: 'a search engine sorts a missing value lowest, the baseline highest, so the null group sits at the other end of a grouped level',
};

/** Every accepted difference of one provider, for reports and for the coverage notes. */
export function divergencesFor(provider: Provider): Divergence[] {
  const found: Divergence[] = NUMBER_RULES.filter((rule) => rule.providers === 'all' || rule.providers.includes(provider));
  if (LAST_ROW_UNKNOWN.providers.includes(provider)) {
    found.push(LAST_ROW_UNKNOWN);
  }
  if (NULL_GROUP_AT_OTHER_END.providers.includes(provider)) {
    found.push(NULL_GROUP_AT_OTHER_END);
  }
  return found;
}

/** The rule that decides a numeric difference for this provider and key, if any. */
export function numberRuleFor(provider: Provider, key: string): NumberRule | undefined {
  return NUMBER_RULES.find((rule) => (rule.providers === 'all' || rule.providers.includes(provider)) && key.endsWith(rule.keySuffix));
}

function roundHalfUp(value: number, places: number): number {
  // The exponent-text form avoids binary multiplication error (1.005 * 100).
  const sign = value < 0 ? -1 : 1;
  return sign * Number(`${Math.round(Number(`${Math.abs(value)}e${places}`))}e-${places}`);
}

/** Whether `actual` is acceptable for `expected` under the rule. */
export function numbersMatchUnder(rule: NumberRule, expected: number, actual: number): boolean {
  const { mode } = rule;
  switch (mode.kind) {
    case 'relative':
      return Math.abs(expected - actual) <= mode.tolerance * Math.max(1, Math.abs(expected), Math.abs(actual));
    case 'truncate':
      return actual === Math.trunc(expected);
    case 'round':
      return actual === roundHalfUp(expected, mode.places);
  }
}
