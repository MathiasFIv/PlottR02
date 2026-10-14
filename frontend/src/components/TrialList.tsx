import { Link } from 'react-router-dom';
import type { TrialSummary } from '../types';

/**
 * Trial status → badge. A newly created trial renders NO badge at all — it
 * previously showed a noisy blue "Created" chip on every row. `InProgress`
 * shows a yellow "In Progress" chip as soon as ratings are being written;
 * `Completed` shows a green "Done" chip once the document is fully plotted.
 */
const TRIAL_STATUS_BADGES: Record<
  string,
  { label: string; tone: string } | undefined
> = {
  InProgress: { label: 'In Progress', tone: 'badge-warning' },
  Completed: { label: 'Done', tone: 'badge-success' },
};

type TrialListProps = {
  trials: TrialSummary[];
  loading: boolean;
  error: string | null;
  /** Optional per-row handler for downloading the trial's current workbook. */
  onDownload?: (trial: TrialSummary) => void;
  /** Trial whose workbook download is currently in flight (disables its row button). */
  downloadingTrialId?: string | null;
  /** Optional per-row handler for opening the crew-assignment dialog. */
  onAssign?: (trial: TrialSummary) => void;
  /** Overrides the default empty-state line when the list has no trials. */
  emptyMessage?: string;
  /**
   * When true, rows whose trial is assigned to the signed-in user show an
   * "Assigned to you" badge. Creators pass this; field workers' lists are
   * assignments-only, so they would see it on every row.
   */
  showAssignedBadge?: boolean;
};

/** Presentational list of trials fetched from GET /api/Trials. */
export function TrialList({
  trials,
  loading,
  error,
  onDownload,
  downloadingTrialId,
  onAssign,
  emptyMessage,
  showAssignedBadge,
}: TrialListProps) {
  if (loading && trials.length === 0) {
    return <p className="text-sm text-base-content/70">Loading trials…</p>;
  }

  if (error) {
    return <p className="text-sm mt-3 text-error">Error: {error}</p>;
  }

  if (trials.length === 0) {
    return (
      <p className="text-sm text-base-content/70">
        {emptyMessage ?? 'No trials uploaded yet.'}
      </p>
    );
  }

  return (
    <ul className="list mt-4">
      {trials.map((trial) => {
        const statusBadge = trial.status
          ? TRIAL_STATUS_BADGES[trial.status]
          : undefined;
        return (
        <li key={trial.trialId} className="list-row">
          <div>
            <p className="font-medium text-base-content">
              {trial.externalId || trial.fileName}
            </p>
            <div className="flex items-center flex-wrap gap-2.5 mt-0.5">
              {statusBadge && (
                <span className={`badge ${statusBadge.tone} badge-sm text-xs`}>
                  {statusBadge.label}
                </span>
              )}
              {showAssignedBadge && trial.isAssignedToCurrentUser && (
                <span className="badge badge-accent badge-sm text-xs">
                  Assigned to you
                </span>
              )}
              <span className="text-sm text-base-content/70">
                {new Date(trial.createdAt).toLocaleString()}
              </span>
              {trial.createdByName && (
                <span className="text-sm text-base-content/70">
                  by {trial.createdByName}
                </span>
              )}
              {onDownload && (
                <button
                  type="button"
                  className="btn btn-link btn-primary btn-sm px-1"
                  disabled={downloadingTrialId === trial.trialId}
                  onClick={() => onDownload(trial)}
                >
                  Download
                </button>
              )}
              {onAssign && (
                <button
                  type="button"
                  className="btn btn-link btn-primary btn-sm px-1"
                  onClick={() => onAssign(trial)}
                >
                  Assign crew
                </button>
              )}
              {/* Read-only sheet route (N2). A non-ARM workbook renders the
                  dedicated readable state on that page rather than failing. */}
              <Link
                className="link link-primary text-sm font-medium"
                to={`/trials/${trial.trialId}/sheet`}
              >
                View sheet
              </Link>
            </div>
          </div>
        </li>
        );
      })}
    </ul>
  );
}
