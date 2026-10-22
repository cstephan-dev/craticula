/** A cell of a CSV file, as text. */
export type CsvTable = string[][];

/**
 * A small RFC 4180 reader: comma separated, double quotes around a cell that holds a comma, a quote or a line break, a doubled
 * quote for a quote inside one. Line breaks (\r\n or \n) between records. The demo's export has a customer name with a line
 * break inside quotes, so splitting on newlines would be wrong.
 */
export function parseCsv(input: string): CsvTable {
  // The demo writes a byte order mark first, so a spreadsheet opens the file as UTF-8; it is not part of the first cell.
  const text = input.startsWith('\uFEFF') ? input.slice(1) : input;
  const records: CsvTable = [];
  let record: string[] = [];
  let cell = '';
  let quoted = false;
  let index = 0;
  const endCell = () => {
    record.push(cell);
    cell = '';
  };
  const endRecord = () => {
    endCell();
    records.push(record);
    record = [];
  };
  while (index < text.length) {
    const char = text[index];
    if (quoted) {
      if (char === '"') {
        if (text[index + 1] === '"') {
          cell += '"';
          index += 1;
        } else {
          quoted = false;
        }
      } else {
        cell += char;
      }
    } else if (char === '"') {
      quoted = true;
    } else if (char === ',') {
      endCell();
    } else if (char === '\r' || char === '\n') {
      if (char === '\r' && text[index + 1] === '\n') {
        index += 1;
      }
      endRecord();
    } else {
      cell += char;
    }
    index += 1;
  }
  if (quoted) {
    throw new Error('The CSV ends inside a quoted cell.');
  }
  if (cell !== '' || record.length > 0) {
    endRecord();
  }
  return records;
}

const DECIMAL = /^-?\d+(\.\d+)?$/;

/** Exact text, except two plain decimals that are the same number: PostgreSQL writes an amount as 20568.00, MongoDB as 20568. */
export function sameCell(expected: string, actual: string): boolean {
  if (expected === actual) {
    return true;
  }
  if (!DECIMAL.test(expected) || !DECIMAL.test(actual)) {
    return false;
  }
  const [expectedWhole = '', expectedFraction = ''] = expected.split('.');
  const [actualWhole = '', actualFraction = ''] = actual.split('.');
  const places = Math.max(expectedFraction.length, actualFraction.length);
  const scaled = (whole: string, fraction: string) => BigInt(`${whole}${fraction.padEnd(places, '0')}`);
  return scaled(expectedWhole, expectedFraction) === scaled(actualWhole, actualFraction);
}

/** The first difference between two tables (header and every row, cell by cell), or null when they are equal. */
export function firstCsvDifference(expected: CsvTable, actual: CsvTable): string | null {
  const common = Math.min(expected.length, actual.length);
  for (let line = 0; line < common; line += 1) {
    const left = expected[line] ?? [];
    const right = actual[line] ?? [];
    if (left.length !== right.length) {
      return `line ${line}: ${left.length} cells expected but got ${right.length}`;
    }
    for (let column = 0; column < left.length; column += 1) {
      if (!sameCell(left[column] ?? '', right[column] ?? '')) {
        return `line ${line}, column ${column}${line > 0 ? ` (${expected[0]?.[column]})` : ''}: expected ${JSON.stringify(left[column])} but got ${JSON.stringify(right[column])}`;
      }
    }
  }
  return expected.length === actual.length ? null : `${expected.length} lines expected but got ${actual.length}`;
}
