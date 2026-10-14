import { useAtomValue } from 'jotai';
import { useRef, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { AssignCrewModal } from '../components/AssignCrewModal';
import { Button } from '../components/Button';
import { RecordLinkPanel } from '../components/RecordLinkPanel';
import { TopBar } from '../components/TopBar';
import { TrialList } from '../components/TrialList';
import { api } from '../lib/api';
import { currentUserAtom } from '../store/atoms';
import { useAuth } from '../hooks/useAuth';
import { useTrials } from '../hooks/useTrials';
import { canCreateTrials } from '../types';
import type { TrialSummary } from '../types';

/**
 * Logged-in dashboard: two even panels in a responsive grid — the record
 * launcher (FIRST) and the trials list. The panels scale to the available
 * screen but are clamped (min 28rem / max 48rem), so the grid stays even at
 * any viewport; the long trials list scrolls inside its own panel, so the
 * page itself never grows unwieldy.
 *
 * User management no longer lives on the dashboard: "Create user" and
 * "Manage users" moved into the shared top bar (see TopBar).
 *
 * The trials panel is read-only for every role. Creators (system admins,
 * researchers, trial managers) see a single "Upload Trial" button UNDER the
 * list: it opens the native file picker and uploads the chosen workbook
 * immediately. Field workers see no upload — their list is server-filtered
 * to their assignments, with a role-appropriate empty message instead.
 *
 * Creators additionally see an "Assigned to you" badge on trials that include
 * them in the crew (`showAssignedBadge={showTrialUpload}`); field workers'
 * lists are assignments-only, so the badge would be noise on every row.
 */
export function DashboardView() {
  const currentUser = useAtomValue(currentUserAtom);
  const navigate = useNavigate();
  const { logout } = useAuth();
  // The trials list is read-only for every role; only creators see the
  // upload button that POSTs to /api/Trials.
  const showTrialUpload = canCreateTrials(currentUser?.role ?? null);
  const {
    trials,
    loading: trialsLoading,
    error: trialsError,
    uploading,
    uploadError,
    fetchTrials,
    createTrial,
  } = useTrials();

  // Trial upload state — the hidden file input is opened by the "Upload
  // Trial" button below the list; picking a file uploads it immediately.
  const fileInputRef = useRef<HTMLInputElement>(null);
  const [uploadedFileName, setUploadedFileName] = useState<string | null>(null);

  // Trial workbook download state
  const [downloadingTrialId, setDownloadingTrialId] = useState<string | null>(
    null,
  );
  const [downloadError, setDownloadError] = useState<string | null>(null);
  const [downloadedFileName, setDownloadedFileName] = useState<string | null>(
    null,
  );

  // Per-trial crew-assignment dialog (managers only).
  const [assigningTrial, setAssigningTrial] = useState<TrialSummary | null>(null);

  function handleLogout() {
    logout();
    navigate('/login', { replace: true });
  }

  function handleFileSelected(e: React.ChangeEvent<HTMLInputElement>) {
    const file = e.target.files?.[0] ?? null;
    // Reset the native input first so picking the same file again re-fires.
    e.target.value = '';
    setUploadedFileName(null);
    if (!file) return;

    // The button is the whole flow: a chosen file uploads right away.
    void createTrial(file).then((result) => {
      if (result) setUploadedFileName(result.fileName);
    });
  }

  async function handleDownload(trial: TrialSummary) {
    setDownloadingTrialId(trial.trialId);
    setDownloadError(null);
    setDownloadedFileName(null);
    try {
      const name = await api.downloadWorkbook(trial.trialId);
      setDownloadedFileName(name);
    } catch (err) {
      setDownloadError(
        err instanceof Error ? err.message : 'Could not download the workbook.',
      );
    } finally {
      setDownloadingTrialId(null);
    }
  }

  return (
    <div>
      <TopBar onLogout={handleLogout} />

      <main className="mx-auto grid w-full max-w-[1440px] gap-6 px-6 py-8 grid-cols-1 md:grid-cols-2">
        {/* The record launcher is always the FIRST panel. */}
        <RecordLinkPanel className="min-w-0 overflow-hidden md:h-[clamp(28rem,calc(100vh_-_9rem),48rem)]" />

        <section className="card card-border bg-base-100 shadow-sm p-6 min-w-0 flex flex-col overflow-hidden md:h-[clamp(28rem,calc(100vh_-_9rem),48rem)]">
          <h2 className="text-lg font-semibold text-base-content mb-3">
            Trials
          </h2>

          {!showTrialUpload && (
            <p className="text-sm text-base-content/70">
              Trials are created by researchers and trial managers.
            </p>
          )}

          <div className="flex-1 min-h-0 overflow-y-auto lg:overscroll-contain lg:pr-1">
            <TrialList
              trials={trials}
              loading={trialsLoading}
              error={trialsError}
              onDownload={handleDownload}
              downloadingTrialId={downloadingTrialId}
              onAssign={showTrialUpload ? (t) => setAssigningTrial(t) : undefined}
              emptyMessage={
                showTrialUpload ? undefined : 'No trials assigned to you yet.'
              }
              showAssignedBadge={showTrialUpload}
            />
          </div>

          {showTrialUpload && (
            <div className="mt-3 flex flex-wrap items-center gap-3">
              {uploadError && (
                <p className="text-sm text-error">{uploadError}</p>
              )}
              {uploadedFileName && (
                <p className="text-sm text-success" role="status">
                  Uploaded "{uploadedFileName}" — the list above has been
                  refreshed.
                </p>
              )}
              <Button
                type="button"
                onClick={() => fileInputRef.current?.click()}
                disabled={uploading}
              >
                {uploading ? 'Uploading…' : 'Upload Trial'}
              </Button>
              <input
                ref={fileInputRef}
                id="trial-file"
                type="file"
                accept=".xlsx"
                className="hidden"
                onChange={handleFileSelected}
              />
            </div>
          )}

          {downloadingTrialId && (
            <p className="text-sm text-base-content/70" role="status">
              Downloading workbook…
            </p>
          )}
          {downloadError && (
            <p className="text-sm mt-3 text-error">{downloadError}</p>
          )}
          {downloadedFileName && (
            <p className="text-sm mt-3 text-success" role="status">
              Downloaded "{downloadedFileName}"
            </p>
          )}
        </section>
      </main>

      {assigningTrial && (
        <AssignCrewModal
          trial={assigningTrial}
          onSaved={() => void fetchTrials()}
          onClose={() => setAssigningTrial(null)}
        />
      )}
    </div>
  );
}