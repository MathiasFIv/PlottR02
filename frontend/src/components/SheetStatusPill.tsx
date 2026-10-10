import { describeSheetStatus } from '../lib/sheetStatus';
import type {
  TrialSheetSource,
  TrialSheetStatus,
  SheetStatusTone,
} from '../lib/sheetStatus';

type SheetStatusPillProps = {
  status: TrialSheetStatus;
  source: TrialSheetSource;
  /** ISO timestamp backing the `saved …` detail (payload's last update or the snapshot's `savedAt`). */
  savedAt: string | null;
};

/** Tone → daisyUI badge modifier (old `.sheet-pill--{tone}` → `badge-soft badge-*`). */
const TONE_BADGE: Record<SheetStatusTone, string> = {
  muted: 'badge-ghost',
  ok: 'badge-success',
  warn: 'badge-warning',
  error: 'badge-error',
};

/**
 * The sheet's status pill (shared by the RecordView panel and the full-sheet
 * route so both read identically). Text and tone come from the pure
 * `describeSheetStatus` — this is render wiring only.
 *
 * Says `offline — showing saved copy` (with the save time) exactly when the
 * screen is backed by a persisted snapshot instead of a live response.
 */
export function SheetStatusPill({ status, source, savedAt }: SheetStatusPillProps) {
  const { label, tone, detail } = describeSheetStatus(status, source, savedAt);
  return (
    <span
      className={`badge badge-sm badge-soft text-xs font-semibold whitespace-nowrap ${TONE_BADGE[tone]}`}
      role="status"
      aria-live="polite"
    >
      {detail === null ? label : `${label} · ${detail}`}
    </span>
  );
}
