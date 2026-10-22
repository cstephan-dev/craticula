import { expect, test } from '@playwright/test';
import { firstCsvDifference, parseCsv, sameCell } from './csv';

// These checks need neither a browser nor the demo.
test('quoted cells keep commas, quotes and line breaks', () => {
  const table = parseCsv('id,customer\r\n1,"O\'Brien ""Big"" Corp"\r\n2,"Müller, Söhne & Co"\r\n3,"Line\nBreak Ltd"\r\n');
  expect(table).toEqual([['id', 'customer'], ['1', 'O\'Brien "Big" Corp'], ['2', 'Müller, Söhne & Co'], ['3', 'Line\nBreak Ltd']]);
});

test('a final record without a line break and an empty cell are kept', () => {
  expect(parseCsv('a,b\n1,')).toEqual([['a', 'b'], ['1', '']]);
});

test('a byte order mark is not part of the first cell', () => {
  expect(parseCsv('\uFEFFId,Customer\n1,Acme')[0]).toEqual(['Id', 'Customer']);
});

test('an unterminated quote is an error', () => {
  expect(() => parseCsv('a\n"open')).toThrow(/quoted cell/);
});

test('plain decimals compare by value, text compares exactly', () => {
  expect(sameCell('20568.00', '20568')).toBe(true);
  expect(sameCell('20568.10', '20568.1')).toBe(true);
  expect(sameCell('20568.01', '20568')).toBe(false);
  expect(sameCell('-1.50', '-1.5')).toBe(true);
  expect(sameCell("'=SUM(A1)", '=SUM(A1)')).toBe(false);
});

test('the first difference names the line and the column', () => {
  const left = [['id', 'amount'], ['1', '5.00'], ['2', '6.00']];
  expect(firstCsvDifference(left, [['id', 'amount'], ['1', '5'], ['2', '6']])).toBeNull();
  expect(firstCsvDifference(left, [['id', 'amount'], ['1', '5'], ['2', '7']])).toContain('line 2, column 1 (amount)');
  expect(firstCsvDifference(left, left.slice(0, 2))).toContain('3 lines expected but got 2');
});
