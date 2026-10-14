/**
 * Two-tier column header for the read-only sheet grid (decision B2 / A12).
 *
 * The header is derived **only** from the server-supplied `columns` array
 * (`GET /api/Trials/{trialId}/sheet`) — never from the workbook or any
 * client-side trait table.
 *
 * The old mis-ordered pilot `TRAITS` table (pest-major: ordinal 2 =
 * SEPTTR × LEAF2, while the workbook is part-major: ordinal 2 = col D =
 * ERYSGR × LEAF1) has been removed from the codebase, so there is no longer
 * any in-repo copy of that wrong mapping to poison the header with. Sheet
 * header identity comes from the file, per column, via the API.
 *
 * Tier 1 — Part Rated runs: consecutive columns sharing a `partRated` value
 * become ONE cell with a `colSpan`. Runs are derived from *consecutive equal
 * values*, never hardcoded to a 3-wide LEAF block: a workbook with 2 or 4
 * columns per part renders the span it actually has.
 *
 * Tier 2 — Pest Code: one cell per column, index-aligned with
 * `row.values[i]` (contract invariant: `values[i] ↔ columns[i]`).
 *
 * The server can legitimately send `null` (or blank) for either field — the
 * reader falls back to null when the workbook cell is missing/empty. Nulls
 * group together into their own run and render as `—` instead of "null".
 */

import type { SheetColumn } from '../types';

/** Placeholder shown when the workbook had no value for a header cell. */
const EMPTY_LABEL = '—';

/** Tier-1 cell: one run of consecutive columns sharing a Part Rated value. */
type SheetHeaderRun = {
  /** Normalized raw value shared by the run (`null` when absent/blank). */
  partRated: string | null;
  /** Display text — never empty (falls back to `—`). */
  label: string;
  /** How many consecutive columns the run spans — always >= 1. */
  span: number;
};

/** Tier-2 cell: the Pest Code of exactly one column. */
type SheetHeaderPest = {
  /** 1..9 — index in `SheetRow.values` is `traitOrdinal - 1`. */
  traitOrdinal: number;
  /** Excel column letter ("C".."K") — used as a tooltip/title. */
  column: string;
  /** Normalized raw pest code (`null` when absent/blank). */
  pestCode: string | null;
  /** Display text — never empty (falls back to `—`). */
  label: string;
};

type SheetHeader = {
  runs: SheetHeaderRun[];
  pests: SheetHeaderPest[];
};

/**
 * Trims a server-supplied header value and folds null/blank into one `null`
 * so `"LEAF1"` and `" LEAF1 "` group together and `null`/`""` never split a
 * run in two.
 */
function normalize(value: string | null): string | null {
  const trimmed = value === null ? '' : value.trim();
  return trimmed === '' ? null : trimmed;
}

/** Display text for a normalized value: the value itself, or `—`. */
function labelFor(normalized: string | null): string {
  return normalized ?? EMPTY_LABEL;
}

/**
 * Builds the two-tier `<thead>` model from the API's `columns`.
 *
 * Pure and deterministic: same columns in → same header out. An empty array
 * yields an empty header (hosts then render their "no columns" state).
 */
export function buildSheetHeader(columns: SheetColumn[]): SheetHeader {
  const runs: SheetHeaderRun[] = [];
  const pests: SheetHeaderPest[] = [];

  for (const column of columns) {
    const pestCode = normalize(column.pestCode);
    pests.push({
      traitOrdinal: column.traitOrdinal,
      column: column.column,
      pestCode,
      label: labelFor(pestCode),
    });

    // Tier 1: extend the previous run only when the value is *equal*, so the
    // span always reflects the file (never a hardcoded 3-wide LEAF block).
    const partRated = normalize(column.partRated);
    const current = runs.length > 0 ? runs[runs.length - 1] : undefined;
    if (current !== undefined && current.partRated === partRated) {
      current.span += 1;
    } else {
      runs.push({ partRated, label: labelFor(partRated), span: 1 });
    }
  }

  return { runs, pests };
}
