import { describe, expect, it } from 'vitest';
import { sheetCellState, sheetCellValue } from './sheetCell';

/**
 * Pins design.md **Rule 2 — empty ≠ 0** at the exact render boundary
 * (PLAN_2026-10-06 §9 risk 4, PLAN_2026-10-07 §10 A-8 D5 value channel).
 * Both halves are asserted: `0` must render as a rated value, `null` must
 * render blank — never `{value || ''}`, which would blank a real `0`.
 */
describe('sheetCellValue', () => {
  it('renders null as blank text', () => {
    expect(sheetCellValue(null)).toBe('');
  });

  it('renders a rated 0 as "0" — empty ≠ 0', () => {
    expect(sheetCellValue(0)).toBe('0');
  });

  it('stringifies positive and negative ratings', () => {
    expect(sheetCellValue(1)).toBe('1');
    expect(sheetCellValue(2.5)).toBe('2.5');
    expect(sheetCellValue(-3)).toBe('-3');
  });
});

describe('sheetCellState', () => {
  it('maps null to the empty (available) channel class', () => {
    expect(sheetCellState(null)).toBe('sheet-cell--empty');
  });

  it('maps a rated 0 to the filled channel class', () => {
    expect(sheetCellState(0)).toBe('sheet-cell--filled');
  });

  it('maps any non-null number to the filled channel class', () => {
    expect(sheetCellState(1)).toBe('sheet-cell--filled');
    expect(sheetCellState(-2.5)).toBe('sheet-cell--filled');
  });
});
