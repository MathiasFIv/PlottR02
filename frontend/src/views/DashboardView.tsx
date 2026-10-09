import { useAtomValue } from 'jotai';
import { useRef, useState } from 'react';
import type { FormEvent } from 'react';
import { Link, useNavigate } from 'react-router-dom';
import { AssignCrewModal } from '../components/AssignCrewModal';
import { Button } from '../components/Button';
import { TrialList } from '../components/TrialList';
import { UserManagementSection } from '../components/UserManagementSection';
import { api } from '../lib/api';
import { currentUserAtom } from '../store/atoms';
import { useAuth } from '../hooks/useAuth';
import { useTrials } from '../hooks/useTrials';
import { canCreateUsers, canCreateTrials, roleLabel } from '../types';
import type { TrialSummary } from '../types';

/**
 * Logged-in dashboard: the trials list (visible to every role; the upload form
 * and per-trial "Assign crew" action are creators-only: system admins,
 * researchers, trial managers) plus a user-administration section (create —
 * and, for managers, list, POST/GET /api/Users) shown to any user creator
 * (system admins, researchers, trial managers). The user list fetch is
 * managers-only inside UserManagementSection, so create-only actors never
 * trigger it.
 *
 * The empty trials list uses a role-appropriate message: creators see the
 * default "no trials uploaded yet" line, while non-creators (field workers,
 * whose list is server-filtered to their assignments) see "No trials assigned
 * to you yet." instead.
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
      <header className="topbar">
        <span className="topbar__brand">PlottR 0.2</span>
        <div className="topbar__user">
          {currentUser && (
            <span className="topbar__identity">
              Signed in as <strong>{currentUser.name}</strong>
              {currentUser.role ? ` (${roleLabel(currentUser.role)})` : ''}
            </span>
          )}
          <Link className="link-button" to="/record">
            Record
          </Link>
          <Button variant="secondary" onClick={handleLogout}>
            Log out
          </Button>
        </div>
      </header>

      <main className="dashboard">
        {showUserManagement && (
          <UserManagementSection actorRole={currentUser?.role ?? null} />
        )}

        <section className="card">
          <div className="card__header">
            <h2 className="heading">Trials</h2>
            <button
              type="button"
              className="link-button"
              onClick={() => void fetchTrials()}
            >
              Refresh
            </button>
          </div>

          {showTrialUpload ? (
            <form onSubmit={handleUpload}>
              <div className="field">
                <label htmlFor="trial-file" className="field__label">
                  Upload .xlsx workbook
                </label>
                <input
                  ref={fileInputRef}
                  id="trial-file"
                  type="file"
                  accept=".xlsx"
                  className="input"
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
            <p className="muted">
              Trials are created by researchers and trial managers.
            </p>
          )}

          {uploadError && <p className="error-text">{uploadError}</p>}
          {uploadedFileName && (
            <p className="success-text" role="status">
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
          />

          {downloadingTrialId && (
            <p className="muted" role="status">
              Downloading workbook…
            </p>
          )}
          {downloadError && <p className="error-text">{downloadError}</p>}
          {downloadedFileName && (
            <p className="success-text" role="status">
              Downloaded "{downloadedFileName}"
            </p>
          )}
        </section>
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