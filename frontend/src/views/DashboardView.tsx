import { useAtomValue } from 'jotai';
import { useRef, useState } from 'react';
import type { FormEvent } from 'react';
import { useNavigate } from 'react-router-dom';
import { AssignCrewModal } from '../components/AssignCrewModal';
import { Button } from '../components/Button';
import { TopBar } from '../components/TopBar';
import { TrialList } from '../components/TrialList';
import { UsersPanel } from '../components/UserManagementSection';
import { api } from '../lib/api';
import { currentUserAtom } from '../store/atoms';
import { useAuth } from '../hooks/useAuth';
import { useTrials } from '../hooks/useTrials';
import { canCreateUsers, canCreateTrials } from '../types';
import type { TrialSummary } from '../types';

/**
 * Logged-in dashboard: the trials list (visible to every role; the upload form
 * and per-trial "Assign crew" action are creators-only: system admins,
 * researchers, trial managers) plus a consolidated users panel (create —
 * and, for managers, list/edit/delete via /api/Users) shown to any user creator
 * (system admins, researchers, trial managers). The panel is laid out in a
 * responsive 3-column grid: the trials section spans two columns and the users
 * panel sits in the third. The user list fetch is managers-only inside
 * UsersPanel, so create-only actors never trigger it.
 *
 * The empty trials list uses a role-appropriate message: creators see the
 * default "no trials uploaded yet" line, while non-creators (field workers,
 * whose list is server-filtered to their assignments) see "No trials assigned
 * to you yet." instead.
 *
 * Creators additionally see an "Assigned to you" badge on trials that include
 * them in the crew (`showAssignedBadge={showTrialUpload}`); field workers'
 * lists are assignments-only, so the badge would be noise on every row.
 */
export function DashboardView() {
  const currentUser = useAtomValue(currentUserAtom);
  const navigate = useNavigate();
  const { logout } = useAuth();
  // Any role allowed to create accounts sees the section; the list within it
  // remains managers-only.
  const showUserManagement = canCreateUsers(currentUser?.role ?? null);
  // The trials list is read-only for every role; only creators see the upload
  // form that POSTs to /api/Trials.
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

  // Trial upload state
  const fileInputRef = useRef<HTMLInputElement>(null);
  const [selectedFile, setSelectedFile] = useState<File | null>(null);
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

  function handleFileChange(e: React.ChangeEvent<HTMLInputElement>) {
    const file = e.target.files?.[0] ?? null;
    setSelectedFile(file);
    setUploadedFileName(null);
  }

  async function handleUpload(event: FormEvent) {
    event.preventDefault();
    if (!selectedFile || uploading) return;

    const result = await createTrial(selectedFile);
    if (result) {
      setUploadedFileName(result.fileName);
      setSelectedFile(null);
      // Reset the native file input so it shows the empty state
      if (fileInputRef.current) {
        fileInputRef.current.value = '';
      }
    }
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
      <TopBar links={[{ to: '/record', label: 'Record' }]} onLogout={handleLogout} />

      <main className="mx-auto grid max-w-[1280px] gap-6 px-6 py-8 lg:grid-cols-3 items-start">
        <section
          className={`card card-border bg-base-100 shadow-sm p-6 ${
            showUserManagement ? 'lg:col-span-2' : 'lg:col-span-3'
          }`}
        >
          <div className="flex items-center justify-between mb-3">
            <h2 className="text-lg font-semibold text-base-content">Trials</h2>
            <button
              type="button"
              className="link link-primary text-sm font-medium bg-transparent border-0 p-0"
              onClick={() => void fetchTrials()}
            >
              Refresh
            </button>
          </div>

          {showTrialUpload ? (
            <form onSubmit={handleUpload}>
              <div className="mb-4">
                <label
                  htmlFor="trial-file"
                  className="block mb-1.5 text-sm font-medium text-base-content/80"
                >
                  Upload .xlsx workbook
                </label>
                <input
                  ref={fileInputRef}
                  id="trial-file"
                  type="file"
                  accept=".xlsx"
                  className="file-input w-full"
                  onChange={handleFileChange}
                />
              </div>
              <Button
                type="submit"
                disabled={!selectedFile || uploading}
              >
                {uploading ? 'Uploading…' : 'Upload trial'}
              </Button>
            </form>
          ) : (
            <p className="text-sm text-base-content/70">
              Trials are created by researchers and trial managers.
            </p>
          )}

          {uploadError && (
            <p className="text-sm mt-3 text-error">{uploadError}</p>
          )}
          {uploadedFileName && (
            <p className="text-sm mt-3 text-success" role="status">
              Uploaded "{uploadedFileName}" — the list below has been refreshed.
            </p>
          )}

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

        {showUserManagement && (
          <UsersPanel actorRole={currentUser?.role ?? null} />
        )}
      </main>

      {assigningTrial && (
        <AssignCrewModal
          trial={assigningTrial}
          onClose={() => setAssigningTrial(null)}
        />
      )}
    </div>
  );
}