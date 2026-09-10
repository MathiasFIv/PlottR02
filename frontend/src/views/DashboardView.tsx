import { useAtomValue } from 'jotai';
import { useRef, useState } from 'react';
import type { FormEvent } from 'react';
import { Link, useNavigate } from 'react-router-dom';
import { Button } from '../components/Button';
import { FormField } from '../components/FormField';
import { TrialList } from '../components/TrialList';
import { UserList } from '../components/UserList';
import { currentUserAtom } from '../store/atoms';
import { useAuth } from '../hooks/useAuth';
import { useTrials } from '../hooks/useTrials';
import { useUsers } from '../hooks/useUsers';

/**
 * Logged-in dashboard: a "create user" form (POST /api/Users) plus the live
 * user list (GET /api/Users).  Initial fetch of the list happens in useUsers.
 */
export function DashboardView() {
  const currentUser = useAtomValue(currentUserAtom);
  const navigate = useNavigate();
  const { logout } = useAuth();
  const { users, loading, error, fetchUsers, createUser } = useUsers();
  const {
    trials,
    loading: trialsLoading,
    error: trialsError,
    uploading,
    uploadError,
    fetchTrials,
    createTrial,
  } = useTrials();

  const [name, setName] = useState('');
  const [email, setEmail] = useState('');
  const [role, setRole] = useState('');
  const [password, setPassword] = useState('');
  const [validationError, setValidationError] = useState<string | null>(null);
  const [created, setCreated] = useState<string | null>(null);
  const [submitting, setSubmitting] = useState(false);

  // Trial upload state
  const fileInputRef = useRef<HTMLInputElement>(null);
  const [selectedFile, setSelectedFile] = useState<File | null>(null);
  const [uploadedFileName, setUploadedFileName] = useState<string | null>(null);

  async function handleSubmit(event: FormEvent) {
    event.preventDefault();
    setValidationError(null);

    const trimmed = name.trim();
    const trimmedEmail = email.trim();
    const passwordError = validatePassword(password);
    if (passwordError) {
      setValidationError(passwordError);
      return;
    }
    if (!trimmed || !trimmedEmail || submitting) {
      return;
    }

    setSubmitting(true);
    const result = await createUser({
      name: trimmed,
      email: trimmedEmail,
      role: role.trim() || null,
      password,
    });
    setSubmitting(false);

    if (result) {
      setCreated(result.name);
      setName('');
      setEmail('');
      setRole('');
      setPassword('');
    }
  }

  /** Empty and overlong passwords would be rejected by the API (400). */
  function validatePassword(value: string): string | null {
    if (!value) {
      return 'Password is required.';
    }
    const byteLength = new TextEncoder().encode(value).length;
    if (byteLength > 72) {
      return 'Password must be 72 bytes or fewer.';
    }
    return null;
  }

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

  return (
    <div>
      <header className="topbar">
        <span className="topbar__brand">PlottR 0.2</span>
        <div className="topbar__user">
          {currentUser && (
            <span className="topbar__identity">
              Signed in as <strong>{currentUser.name}</strong>
              {currentUser.role ? ` (${currentUser.role})` : ''}
            </span>
          )}
          <Link className="link-button" to="/record">
            Record
          </Link>
          <Link className="link-button" to="/ratings">
            Ratings
          </Link>
          <Button variant="secondary" onClick={handleLogout}>
            Log out
          </Button>
        </div>
      </header>

      <main className="dashboard">
        <section className="card">
          <h2 className="heading">Create a user</h2>
          <form onSubmit={handleSubmit}>
            <FormField
              label="Name"
              htmlFor="create-name"
              inputProps={{
                value: name,
                onChange: (e) => setName(e.target.value),
                placeholder: 'Full name',
                required: true,
              }}
            />
            <FormField
              label="Email"
              htmlFor="create-email"
              inputProps={{
                value: email,
                onChange: (e) => setEmail(e.target.value),
                placeholder: 'you@example.com',
                type: 'email',
                autoComplete: 'email',
                required: true,
              }}
            />
            <FormField
              label="Role (optional)"
              htmlFor="create-role"
              inputProps={{
                value: role,
                onChange: (e) => setRole(e.target.value),
                placeholder: 'e.g. Researcher',
              }}
            />
            <FormField
              label="Password"
              htmlFor="create-password"
              inputProps={{
                value: password,
                onChange: (e) => setPassword(e.target.value),
                placeholder: 'Enter a password',
                type: 'password',
                autoComplete: 'new-password',
              }}
            />
            <Button
              type="submit"
              disabled={!name.trim() || !email.trim() || !password || submitting}
            >
              {submitting ? 'Creating…' : 'Create user'}
            </Button>
          </form>

          {validationError && <p className="error-text">{validationError}</p>}
          {created && (
            <p className="success-text" role="status">
              Created user “{created}” — the list below has been refreshed.
              Password stored hashed (BCrypt) — plaintext is never persisted.
            </p>
          )}
          {error && <p className="error-text">{error}</p>}
        </section>

        <section className="card">
          <div className="card__header">
            <h2 className="heading">Users</h2>
            <button
              type="button"
              className="link-button"
              onClick={() => void fetchUsers()}
            >
              Refresh
            </button>
          </div>
          <UserList users={users} loading={loading} error={error} />
        </section>

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
          />
        </section>
      </main>
    </div>
  );
}