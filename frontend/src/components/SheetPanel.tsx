import { useAtomValue } from 'jotai';
import { useEffect } from 'react';
import { useTrialSheet } from '../hooks/useTrialSheet';
import { deriveEndOfSheet, deriveNextTarget } from '../lib/nextTarget';
import { isNonArmSheetError } from '../lib/sheetStatus';
import { rehydrateSheetTarget, targetHighlight } from '../lib/sheetTarget';
import { sheetLastWrittenAtom } from '../store/atoms';
import { SheetGrid } from './SheetGrid';
import { SheetMetaStrip } from './SheetMetaStrip';
import { SheetStates } from './SheetStates';
import { SheetStatusPill } from './SheetStatusPill';

type SheetPanelProps = {
  trialId: string;
};

/**
 * The RecordView embed wrapper (D2 / §6): the sheet is visible *while*
 * recording. It owns its whole lifecycle — status pill, metadata strip,
 * manual Refresh and the grid — from the `trialId` prop alone, so it never
 * reaches into RecordView's state; switching trials in the picker changes the
 * prop, which clears the sheet atoms, refetches and rehydrates the snapshot.
 *
 * **N2 shows ALL rows** (deliberate transitional state — it validates the
 * phone layout one phase early). N3 narrows this to the focused view
 * (active plot ± 1) and adds the `[This plot | All rows]` toggle: that phase
 * only has to swap the `rows` passed to `SheetGrid` below.
 *
 * The attention badge stays in the mic card; this panel adds nothing but its
 * own status pill, so the two never compete.
 *
 * The N3 current-cell highlight is derived DURING RENDER from
 * `sheetLastWrittenAtom` (the last-written pair, trial-guarded) + the payload
 * on screen: `deriveNextTarget` walks `rows[]` to the row AFTER the written
 * one (crossing plot boundaries, `null` at the end of the sheet) and
 * `targetHighlight` maps it onto the grid, so a refresh or snapshot hydrate
 * re-derives it for free and the persisted pair is re-seeded by the effect
 * below (mount / trial change).
 */
export function SheetPanel({ trialId }: SheetPanelProps) {
  const { sheet, status, source, error, updatedAt, refresh, refreshing } =
    useTrialSheet(trialId);
  /** N3 last-written pair — read live; written by the sync engine (non-React set). */
  const target = useAtomValue(sheetLastWrittenAtom);
  /** Only THIS trial's pair may tint this trial's grid (plot numbers repeat). */
  const forTrial = target?.trialId === trialId ? target : null;

  // Seed / re-seed from the persisted last-written target on mount
  // and on trial change (the atom survives route changes; storage is the
  // source of truth after a reload).
  useEffect(() => {
    rehydrateSheetTarget(trialId);
  }, [trialId]);

  const columns = sheet?.columns ?? null;
  const rows = sheet?.trialId === trialId ? sheet.rows : null;
  // A non-ARM 400 wins over the copy: the server says the sheet is gone.
  // So does a `not-found`: a stale snapshot behind a 404 must render the
  // not-found state, not a grid under a "not found" pill.
  const hasPayload =
    columns !== null &&
    rows !== null &&
    status !== 'not-found' &&
    !isNonArmSheetError(error);

  // Next cell = the row after the last-written one in `rows[]` order
  // (payload-derived — never arithmetic, never a guessed pair).
  const next = rows !== null ? deriveNextTarget(rows, forTrial) : null;
  const endOfSheet = deriveEndOfSheet(rows, forTrial);

  return (
    <div className="sheet-panel">
      <div className="sheet-panel__bar">
        <h2 className="heading sheet-panel__title">Sheet</h2>
        <SheetStatusPill status={status} source={source} savedAt={updatedAt} />
        <button
          type="button"
          className="link-button"
          onClick={refresh}
          disabled={refreshing}
        >
          Refresh
        </button>
      </div>

      {sheet?.metadata != null && <SheetMetaStrip metadata={sheet.metadata} />}

      <SheetStates
        status={status}
        error={error}
        hasPayload={hasPayload}
        rowCount={hasPayload && rows !== null ? rows.length : null}
      />

      {hasPayload && rows !== null && rows.length > 0 && columns !== null && (
        <SheetGrid
          columns={columns}
          rows={rows}
          highlight={targetHighlight(next, columns, rows)}
        />
      )}

      {hasPayload && rows !== null && endOfSheet !== null && (
        <p className="muted" role="status">
          End of sheet — last: Plot {endOfSheet.plot} · Sub {endOfSheet.subsample}
        </p>
      )}
    </div>
  );
}
