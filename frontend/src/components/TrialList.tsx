import { Link } from 'react-router-dom';
import type { TrialSummary } from '../types';

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
    <ul className="list-none mt-4 p-0 border border-base-300 rounded-md bg-base-100 overflow-hidden divide-y divide-base-300">
      {trials.map((trial) => (
        <li
          key={trial.trialId}
          className="flex items-center justify-between gap-3 px-4 py-3"
        >
          <div>
            <p className="font-medium text-base-content">{trial.fileName}</p>
            <div className="flex items-center flex-wrap gap-2.5 mt-0.5">
              <span className="badge badge-info badge-sm text-xs">
                {trial.status}
              </span>
              {showAssignedBadge && trial.isAssignedToCurrentUser && (
                <span className="badge badge-accent badge-sm text-xs">
                  Assigned to you
                </span>
              )}
              <span className="text-sm text-base-content/70">
                {trial.externalId}
              </span>
              <span className="text-sm text-base-content/70">
                {new Date(trial.createdAt).toLocaleString()}
              </span>
              {trial.createdBy && (
                <span className="text-sm text-base-content/70">
                  by {trial.createdBy}
                </span>
              )}
              {onDownload && (
                <button
                  type="button"
                  className="link link-primary text-sm font-medium bg-transparent border-0 p-0"
                  disabled={downloadingTrialId === trial.trialId}
                  onClick={() => onDownload(trial)}
                >
                  Download
                </button>
              )}
              {onAssign && (
                <button
                  type="button"
                  className="link link-primary text-sm font-medium bg-transparent border-0 p-0"
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
          <code className="text-xs break-all text-base-content/70 font-mono">
            {trial.trialId}
          </code>
        </li>
      ))}
    </ul>
  );
}
