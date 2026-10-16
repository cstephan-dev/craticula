import { ApiError } from './api';

/**
 * AG Grid's "In range" filter option, translated for the library.
 *
 * The library refuses a native range (`{type: "inRange", filter, filterTo}` or `{dateFrom, dateTo}`): the wire carries no
 * inclusivity and SEM-4 forbids guessing one. A host that wants ranges sends them through the `customFilters` extension with the
 * inclusivity it means. This demo means **both bounds exclusive**, which is AG Grid's own client-side default
 * (`inRangeInclusive: false`), so the demo shows what a client-side grid would show on the same data.
 *
 * Shared by both pages, so it imports nothing from ag-grid-enterprise (communityImports.test.ts walks the Community entry's imports).
 */

/** The IR typed-value kinds of the demo's schema (orders.schema.json); inRange.test.ts compares this map with the schema file. */
export type FieldType = 'string' | 'int64' | 'decimal' | 'double' | 'boolean' | 'date' | 'datetime';

export const FIELD_TYPES: Readonly<Record<string, FieldType>> = {
  id: 'int64',
  orderNo: 'string',
  customer: 'string',
  country: 'string',
  category: 'string',
  status: 'string',
  amount: 'decimal',
  quantity: 'int64',
  orderedAt: 'datetime',
  shippedOn: 'date',
  isPriority: 'boolean',
};

type FilterModel = Record<string, unknown>;
type Condition = Record<string, unknown>;

/** A node of the library's IR (spec/ir.schema.json) as far as this page builds one. */
export type CustomFilterNode = Record<string, unknown>;

export interface Translated {
  /** The grid's filter model without the in-range conditions (a new object; the input is never changed). */
  filterModel: FilterModel;
  /** The in-range conditions as IR, or undefined when there were none. */
  customFilters?: CustomFilterNode;
}

/** Raised for a filter the demo cannot translate; shown in the banner like any API error. */
export function unsupported(message: string): ApiError {
  return new ApiError(400, 'unsupported-filter', message);
}

const isInRange = (condition: unknown): boolean =>
  typeof condition === 'object' && condition !== null && (condition as Condition).type === 'inRange';

/** The conditions of one column's model, and how they are joined (`undefined` for a single condition). */
function conditionsOf(model: Condition): { list: Condition[]; operator?: string } {
  if (Array.isArray(model.conditions)) {
    return { list: model.conditions as Condition[], operator: model.operator as string | undefined };
  }
  if (model.condition1 !== undefined && model.condition2 !== undefined) {
    return { list: [model.condition1 as Condition, model.condition2 as Condition], operator: model.operator as string | undefined };
  }
  return { list: [model] };
}

const present = (value: unknown): boolean => value !== null && value !== undefined && value !== '';

function typedValue(field: string, type: FieldType | undefined, raw: unknown): Record<string, unknown> {
  switch (type) {
    case 'int64':
      if (typeof raw === 'number' && Number.isSafeInteger(raw)) {
        return { type: 'int64', value: raw };
      }
      throw unsupported(`'${field}' is a whole-number column: a range bound must be a whole number (got ${String(raw)}).`);
    case 'decimal': {
      const text = typeof raw === 'number' ? String(raw) : typeof raw === 'string' ? raw.trim() : '';
      if (/^-?[0-9]+(\.[0-9]+)?$/.test(text)) {
        return { type: 'decimal', value: text };
      }
      throw unsupported(`'${field}' is a decimal column: a range bound must be a plain decimal number (got ${String(raw)}).`);
    }
    case 'date': {
      const match = typeof raw === 'string' ? /^(\d{4}-\d{2}-\d{2})/.exec(raw) : null;
      if (match) {
        return { type: 'date', value: match[1] };
      }
      throw unsupported(`'${field}' is a date column: a range bound must start with YYYY-MM-DD (got ${String(raw)}).`);
    }
    case 'datetime': {
      const match = typeof raw === 'string' ? /^(\d{4}-\d{2}-\d{2})(?:[ T](\d{2}:\d{2})(?::(\d{2}))?)?/.exec(raw) : null;
      if (match) {
        // Read as UTC: the demo API parses dates with DefaultZone = UTC.
        return { type: 'datetime', value: `${match[1]}T${match[2] ?? '00:00'}:${match[3] ?? '00'}Z`, zone: 'UTC' };
      }
      throw unsupported(`'${field}' is a date-time column: a range bound must start with YYYY-MM-DD (got ${String(raw)}).`);
    }
    default:
      throw unsupported(`'${field}' has no numeric or date type: "In range" is not offered on it.`);
  }
}

/** One in-range condition as IR, or undefined when neither bound is filled in. */
function rangeNode(field: string, condition: Condition): CustomFilterNode | undefined {
  const type = FIELD_TYPES[field];
  const isDate = condition.filterType === 'date';
  const fromRaw = isDate ? condition.dateFrom : condition.filter;
  const toRaw = isDate ? condition.dateTo : condition.filterTo;
  const hasFrom = present(fromRaw);
  const hasTo = present(toRaw);

  if (hasFrom && hasTo) {
    return {
      type: 'comparison',
      field,
      op: 'inRange',
      from: typedValue(field, type, fromRaw),
      to: typedValue(field, type, toRaw),
      fromInclusive: false,
      toInclusive: false,
    };
  }
  if (hasFrom) {
    return { type: 'comparison', field, op: 'gt', value: typedValue(field, type, fromRaw) };
  }
  if (hasTo) {
    return { type: 'comparison', field, op: 'lt', value: typedValue(field, type, toRaw) };
  }
  return undefined;
}

/**
 * Moves every in-range condition of `filterModel` into one IR tree for `customFilters`; everything else stays in the grid's own
 * filter model, so the library keeps handling every other operator exactly as before.
 *
 * - A range with both bounds becomes an exclusive range; with only one bound, a `gt` (lower bound) or `lt` (upper bound); with
 *   none, it is dropped.
 * - A column whose conditions are joined by AND: its ranges move out, its other conditions stay.
 * - A column whose conditions are joined by OR and include a range is refused (`unsupported-filter`): translating it would mean
 *   re-implementing every other operator here.
 */
export function translateInRange(filterModel: FilterModel | null | undefined): Translated {
  const kept: FilterModel = {};
  const nodes: CustomFilterNode[] = [];

  for (const [field, raw] of Object.entries(filterModel ?? {})) {
    const model = raw as Condition;
    const { list, operator } = conditionsOf(model);
    if (!list.some(isInRange)) {
      kept[field] = raw;
      continue;
    }
    if (list.length > 1 && operator !== 'AND') {
      throw unsupported(`'${field}': "In range" cannot be combined with another condition by OR in this demo; use AND.`);
    }
    const rest: Condition[] = [];
    for (const condition of list) {
      if (isInRange(condition)) {
        const node = rangeNode(field, condition);
        if (node) {
          nodes.push(node);
        }
      } else {
        rest.push(condition);
      }
    }
    if (rest.length === 1) {
      kept[field] = rest[0];
    } else if (rest.length > 1) {
      kept[field] = { filterType: model.filterType, operator: 'AND', conditions: rest };
    }
  }

  if (nodes.length === 0) {
    return { filterModel: kept };
  }
  return { filterModel: kept, customFilters: nodes.length === 1 ? nodes[0] : { type: 'and', nodes } };
}
