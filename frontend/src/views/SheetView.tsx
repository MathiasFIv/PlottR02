import { useAtom, useAtomValue } from 'jotai';
import { useEffect } from 'react';
import { Link, useNavigate, useParams } from 'react-router-dom';
import { SheetGrid } from '../components/SheetGrid';
import { SheetMetaStrip } from '../components/SheetMetaStrip';
import { SheetStates } from '../components/SheetStates';
import { SheetStatusPill } from '../components/SheetStatusPill';
import { TopBar } from '../components/TopBar';
import { useAuth } from '../hooks/useAuth';
import { useTrialSheet } from '../hooks/useTrialSheet';
import { deriveEndOfSheet, deriveNextTarget } from '../lib/nextTarget';
import {
  decideAnchorTap,
  formatAnchor,
  isAnchorForTrial,
} from '../lib/sheetAnchor';
import type { SheetCellTap } from '../lib/sheetAnchor';
import { isNonArmSheetError } from '../lib/sheetStatus';
import {
  rehydrateSheetTarget,
  targetHighlight,
  targetSelection,
} from '../lib/sheetTarget';
import {
  sheetAnchorNoticeAtom,
  sheetLastWrittenAtom,
  sheetSelectedAtom,
} from '../store/atoms';

/**
 * The dedicated full-sheet route (`/trials/:trialId/sheet`, behind
 * `RequireAuth`) — the second host for the read-only grid, for browsing a
 * whole region. It runs the exact same `useTrialSheet` lifecycle and renders
 * the exact same `SheetGrid` as the RecordView panel: one component, one
 * visual, two hosts (they are mutually exclusive routes — see the atom block
 * in `store/atoms.ts`).
 *
 * The trial id comes from the route params, so a malformed id is just a
 * `not-found`. A 400 from a non-ARM workbook renders the dedicated readable
 * state (PLAN_2026-10-06 §9 risk 9), never a generic error.
 *
 * N4 will add polling to the hook; nothing here needs to change for it.
 */
export function SheetView() {
  const navigate = useNavigate();
  const { logout } = useAuth();
  const { trialId } = useParams<{ trialId: string }>();
  const { sheet, status, source, error, updatedAt } =
    useTrialSheet(trialId ?? null);

  /** N3 last-written pair — read live; written by the sync engine (non-React set). */
  const target = useAtomValue(sheetLastWrittenAtom);
  /**
   * Equality guard: `trialId` is `string | undefined` here. With a defined id
   * only a pair from the SAME trial passes; with `undefined` the comparison
   * is `undefined === undefined` only when `target` is null — so `forTrial`
   * can never be a foreign trial's pair.
   */
  const forTrial = target?.trialId === trialId ? target : null;

  /** Write anchor — read AND written here (tap gesture); syncQueue only reads it. */
  const [anchor, setAnchor] = useAtom(sheetSelectedAtom);
  /** Tap feedback ('cell-filled' refusal) rendered next to the grid. */
  const [anchorNotice, setAnchorNotice] = useAtom(sheetAnchorNoticeAtom);
  /**
   * Trial guards: `trialId` is `string | undefined` here. The anchor guard
   * takes `?? null`, so an id-less route can never match a stored tap; the
   * notice uses the same identity comparison the last-written pair uses.
   */
  const forTrialAnchor = isAnchorForTrial(anchor, trialId ?? null);
  const forTrialNotice =
    anchorNotice !== null && anchorNotice.trialId === trialId
      ? anchorNotice
      : null;

  // Re-seed from the persisted last-written target on mount
  // and on route/trial change (a missing id rehydrates to "no current cell").
  useEffect(() => {
    rehydrateSheetTarget(trialId ?? '');
  }, [trialId]);

  const columns = sheet?.columns ?? null;
  // Explicit null check (not `sheet?.trialId === trialId`): `trialId` is
  // `string | undefined` here, so the optional-chaining comparison is also
  // true when BOTH sides are undefined and does not narrow `sheet`.
  const rows = sheet !== null && sheet.trialId === trialId ? sheet.rows : null;
  // Next cell = the row after the last-written one in `rows[]` order
  // (payload-derived — never arithmetic, never a guessed pair).
  const next = rows !== null ? deriveNextTarget(rows, forTrial) : null;
  const endOfSheet = deriveEndOfSheet(rows, forTrial);
  // No param ⇒ no trial to load: treat it as the same "not found" the API
  // would answer with, instead of a misleading "not loaded yet".
  const viewStatus = trialId === undefined ? 'not-found' : status;
  // `not-found` suppresses the copy too (same rule as the panel host): a
  // stale snapshot behind a 404 shows the not-found state, not a grid.
  const hasPayload =
    trialId !== undefined &&
    viewStatus !== 'not-found' &&
    columns !== null &&
    rows !== null &&
    !isNonArmSheetError(error);

  /**
   * Tap-to-anchor — the SAME behaviour as the embedded `SheetPanel` host:
   * the decision itself is shared in `lib/sheetAnchor.ts#decideAnchorTap`
   * (one grid, two hosts — no duplicated rules), this host only guards the
   * id-less route and applies the result: filled → REFUSE with NO anchor
   * change + `'cell-filled'` notice; empty → anchor THIS trial's tap; the
   * selected cell tapped again → clear. Never touches
   * `sheetLastWrittenAtom` (a tap is not a written fact).
   */
  function handleSelectCell(cell: SheetCellTap): void {
    if (trialId === undefined) return; // no trial ⇒ no anchor to set
    const decision = decideAnchorTap(cell, forTrialAnchor, trialId);
    if (decision.kind === 'refuse-filled') {
      // Refusal only — `setAnchor` is deliberately NOT called here.
      setAnchorNotice({ trialId, kind: 'cell-filled' });
      return;
    }
    setAnchorNotice(null); // an accepted tap retires any refusal notice
    setAnchor(decision.kind === 'clear' ? null : decision.anchor);
  }

  function handleLogout() {
    logout();
    navigate('/login', { replace: true });
  }

  return (
    <div>
      <TopBar onLogout={handleLogout} />

      <main className="mx-auto grid max-w-[1200px] gap-4 px-4 pt-6 pb-12 content-start">
        <p>
          <Link
            className="link link-primary text-sm font-medium bg-transparent border-0 p-0"
            to="/"
          >
            ← Back to dashboard
          </Link>
        </p>

        <section className="card card-border bg-base-100 shadow-sm p-6 flex flex-col gap-4 min-w-0">
          <div className="flex items-center gap-3 flex-wrap">
            <h1 className="text-[28px] font-bold text-base-content mr-auto">
              Trial sheet
            </h1>
            <SheetStatusPill status={viewStatus} source={source} savedAt={updatedAt} />
          </div>

          {sheet?.metadata != null && <SheetMetaStrip metadata={sheet.metadata} />}

          <SheetStates
            status={viewStatus}
            error={error}
            hasPayload={hasPayload}
            rowCount={hasPayload && rows !== null ? rows.length : null}
          />

          {/* Write anchor status — ABOVE the grid so it stays visible while
              the grid itself is scrolled (same `muted` + role=status
              convention as the End-of-sheet line below). Deliberately NOT
              gated on `hasPayload`: the tap survives an offline/failed
              fetch exactly like the atom does. */}
          {forTrialAnchor !== null && (
            <p className="text-sm text-base-content/70" role="status">
              Anchored: {formatAnchor(forTrialAnchor)} — speak numbers to
              fill. Tap the cell again to clear.
            </p>
          )}
          {forTrialNotice !== null && (
            <p className="text-sm font-medium text-warning" role="status">
              Cell already filled
            </p>
          )}

          {hasPayload && rows !== null && rows.length > 0 && columns !== null && (
            <SheetGrid
              columns={columns}
              rows={rows}
              highlight={targetHighlight(next, columns, rows)}
              selected={targetSelection(forTrialAnchor, columns, rows)}
              onSelectCell={handleSelectCell}
            />
          )}

          {hasPayload && rows !== null && endOfSheet !== null && (
            <p className="text-sm text-base-content/70" role="status">
              End of sheet — last: Plot {endOfSheet.plot} · Sub {endOfSheet.subsample}
            </p>
          )}
        </section>
      </main>
    </div>
  );
}
