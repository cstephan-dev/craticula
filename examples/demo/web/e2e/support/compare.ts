import { test } from '@playwright/test';
import type { Provider } from '../../src/api';
import {
  LAST_ROW_UNKNOWN,
  NULL_GROUP_AT_OTHER_END,
  numberRuleFor,
  numbersMatchUnder,
  type Divergence,
  type Row,
} from './divergences';
import { BASELINE } from './providers';

/** What a rows answer carries that the specs compare. */
export interface RowsShape {
  rows: Row[];
  lastRow?: number | null;
}

export interface Comparison {
  ok: boolean;
  /** Empty when `ok`; otherwise names the scenario, the provider and the first difference. */
  message: string;
  /** The accepted divergences that made an otherwise different answer equal, with their reasons. */
  applied: Divergence[];
}

export interface CompareArgs {
  /** Which click-through the answers belong to; it leads the failure message. */
  scenario: string;
  provider: Provider;
  baselineProvider?: Provider;
  baseline: RowsShape;
  actual: RowsShape;
}

type Applied = Map<string, Divergence>;

function isNumber(value: unknown): value is number {
  return typeof value === 'number';
}

/** Exact, except the numbers the provider's accepted rules cover. */
function sameValue(expected: unknown, actual: unknown, key: string, provider: Provider, applied: Applied): boolean {
  if (isNumber(expected) && isNumber(actual)) {
    if (expected === actual) {
      return true;
    }
    const rule = numberRuleFor(provider, key);
    if (rule && numbersMatchUnder(rule, expected, actual)) {
      applied.set(rule.id, rule);
      return true;
    }
    return false;
  }
  if (Array.isArray(expected) && Array.isArray(actual)) {
    return expected.length === actual.length && expected.every((item, index) => sameValue(item, actual[index], key, provider, applied));
  }
  if (expected !== null && actual !== null && typeof expected === 'object' && typeof actual === 'object') {
    const left = expected as Row;
    const right = actual as Row;
    const keys = Object.keys(left);
    return keys.length === Object.keys(right).length && keys.every((k) => k in right && sameValue(left[k], right[k], k, provider, applied));
  }
  return expected === actual;
}

function describe(value: unknown): string {
  return JSON.stringify(value) ?? String(value);
}

/** The first difference between two lists of rows, or null when they are equal. */
function firstDifference(expected: Row[], actual: Row[], provider: Provider, applied: Applied): string | null {
  const common = Math.min(expected.length, actual.length);
  for (let index = 0; index < common; index += 1) {
    const left = expected[index];
    const right = actual[index];
    for (const column of new Set([...Object.keys(left), ...Object.keys(right)])) {
      if (!(column in right)) {
        return `row ${index}: column "${column}" is missing`;
      }
      if (!(column in left)) {
        return `row ${index}: unexpected column "${column}"`;
      }
      if (!sameValue(left[column], right[column], column, provider, applied)) {
        return `row ${index}, column "${column}": expected ${describe(left[column])} but got ${describe(right[column])}`;
      }
    }
  }
  return expected.length === actual.length ? null : `${expected.length} rows expected but got ${actual.length}`;
}

function isNullGroup(row: Row): boolean {
  return Object.values(row)[0] === null;
}

/** A grouped level where only the null group moved to the other end; every other row, and the null rows, are equal in order. */
function nullGroupAtOtherEnd(expected: Row[], actual: Row[], provider: Provider, applied: Applied): boolean {
  if (expected.length === 0 || [...expected, ...actual].some((row) => Object.keys(row).length === 0)) {
    return false;
  }
  const expectedNull = expected.filter(isNullGroup);
  const actualNull = actual.filter(isNullGroup);
  if (expectedNull.length === 0 || expectedNull.length !== actualNull.length) {
    return false;
  }
  const atAnEnd =
    actual.slice(0, actualNull.length).every(isNullGroup) || actual.slice(actual.length - actualNull.length).every(isNullGroup);
  const rest = firstDifference(expected.filter((row) => !isNullGroup(row)), actual.filter((row) => !isNullGroup(row)), provider, applied);
  const nulls = firstDifference(expectedNull, actualNull, provider, applied);
  return atAnEnd && rest === null && nulls === null;
}

/** Compares one provider's rows answer with the baseline's, under the accepted divergences of `divergences.ts`. */
export function compareRows({ scenario, provider, baselineProvider = BASELINE, baseline, actual }: CompareArgs): Comparison {
  const applied: Applied = new Map();
  const fail = (detail: string): Comparison => ({
    ok: false,
    message: `Scenario "${scenario}": ${provider} differs from ${baselineProvider}: ${detail}`,
    applied: [...applied.values()],
  });

  const expectedLast = baseline.lastRow ?? null;
  const actualLast = actual.lastRow ?? null;
  if (expectedLast !== actualLast) {
    const unknownAboveCap =
      actualLast === null &&
      expectedLast !== null &&
      LAST_ROW_UNKNOWN.providers.includes(provider) &&
      expectedLast > LAST_ROW_UNKNOWN.above;
    if (!unknownAboveCap) {
      return fail(`lastRow expected ${describe(expectedLast)} but got ${describe(actualLast)}`);
    }
    applied.set(LAST_ROW_UNKNOWN.id, LAST_ROW_UNKNOWN);
  }

  const difference = firstDifference(baseline.rows, actual.rows, provider, applied);
  if (difference !== null) {
    if (NULL_GROUP_AT_OTHER_END.providers.includes(provider) && nullGroupAtOtherEnd(baseline.rows, actual.rows, provider, applied)) {
      applied.set(NULL_GROUP_AT_OTHER_END.id, NULL_GROUP_AT_OTHER_END);
    } else {
      return fail(difference);
    }
  }
  return { ok: true, message: '', applied: [...applied.values()] };
}

/**
 * The assertion the specs use: throws with provider, scenario and the first difference when the answers differ. Every
 * accepted divergence that was needed is attached to the test as an annotation with its reason, so the report shows it.
 */
export function expectSameRows(args: CompareArgs): void {
  const result = compareRows(args);
  for (const divergence of result.applied) {
    test.info().annotations.push({ type: `divergence: ${args.provider}`, description: `${args.scenario}: ${divergence.id}, ${divergence.reason}` });
  }
  if (!result.ok) {
    throw new Error(result.message);
  }
}
