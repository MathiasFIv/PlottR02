import type { ReactNode } from 'react';
import { isNonArmSheetError } from '../lib/sheetStatus';
import type { TrialSheetStatus } from '../lib/sheetStatus';

type SheetStatesProps = {
  status: TrialSheetStatus;
  error: string | null;
  /** True when a payload (columns + rows) exists AND may be shown. */
  hasPayload: boolean;
  /** `rows.length` when a payload is on screen, else null. */
  rowCount: number | null;
};

/**
 * Every non-grid state of the sheet — loading, idle, error, not-found, the
 * non-ARM 400, the offline notice and the empty-sheet case — as ONE shared
 * block so the embedded panel and the full-sheet route can never drift apart.
 * Renders nothing when there are rows to show.
 *
 * The non-ARM branch (a workbook without a `Subsample Plot Data` sheet) is a
 * dedicated, readable explanation — never a generic error (PLAN_2026-10-06
 * §9 risk 9, E2E sweep step (h)).
 */
export function SheetStates({
  status,
  error,
  hasPayload,
  rowCount,
}: SheetStatesProps) {
  const nonArm = isNonArmSheetError(error);

  if (!hasPayload) {
    if (nonArm) {
      return (
        <div className="sheet-state" role="status">
          <p className="heading">No ratings sheet in this workbook</p>
          <p className="muted">
            The sheet “Subsample Plot Data” was not found in this workbook, so
            there is nothing to show. This is not a server fault — upload an ARM
            ratings workbook to see the grid here.
          </p>
        </div>
      );
    }
    if (status === 'not-found') {
      return (
        <p className="error-text" role="alert">
          This trial or its workbook could not be found.
        </p>
      );
    }
    if (status === 'error') {
      // An Error can carry an empty message — never render a blank card.
      const detail = error !== null && error.trim() !== ''
        ? error
        : 'Could not load the sheet.';
      return (
        <p className="error-text" role="alert">
          {detail}
        </p>
      );
    }
    if (status === 'loading') {
      return (
        <p className="muted" role="status">
          Loading sheet…
        </p>
      );
    }
    if (status === 'idle') {
      return (
        <p className="muted" role="status">
          The sheet is not loaded yet.
        </p>
      );
    }
    // Contract violation: a settled status (`live`/`offline`) with no payload
    // behind it — the hook can mark `live` without ever receiving a payload
    // (useTrialSheet.ts fast path). Never an empty screen under a green pill.
    return (
      <div className="sheet-state" role="status">
        <p>Nothing to show for this sheet yet.</p>
      </div>
    );
  }

  // Payload on screen: the offline notice and the empty-sheet notice are not
  // mutually exclusive (a saved copy can be empty), so collect them.
  const messages: ReactNode[] = [];
  if (status === 'offline') {
    messages.push(
      <p className="warning-text" role="status" key="offline">
        Could not reach the server — showing the saved copy.
      </p>,
    );
  }
  if (rowCount === 0) {
    messages.push(
      <p className="muted" role="status" key="empty">
        This sheet has no rating rows yet.
      </p>,
    );
  }
  return messages.length > 0 ? <>{messages}</> : null;
}
