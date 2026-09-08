import { useAtomValue, useSetAtom } from 'jotai';
import { useEffect, useState } from 'react';
import type { ReactNode } from 'react';
import {
  BrowserRouter,
  Navigate,
  Route,
  Routes,
} from 'react-router-dom';
import { api } from './lib/api';
import { tokens } from './lib/tokens';
import { currentUserAtom, isAuthenticatedAtom } from './store/atoms';
import { LoginView } from './views/LoginView';
import { DashboardView } from './views/DashboardView';

// ---------------------------------------------------------------------------
// Session restore — re-fetches the user profile from /api/Users/me when a
// persisted access token exists (i.e. after a page refresh).
// ---------------------------------------------------------------------------

function useRestoreSession() {
  const setCurrentUser = useSetAtom(currentUserAtom);
  const [restoring, setRestoring] = useState(() => !!tokens.getAccessToken());

  useEffect(() => {
    if (!tokens.getAccessToken()) {
      setRestoring(false);
      return;
    }

    let cancelled = false;

    (async () => {
      try {
        const user = await api.getMe();
        if (!cancelled) setCurrentUser(user);
      } catch {
        // Token was invalid / expired and refresh also failed — clean up
        if (!cancelled) tokens.clear();
      } finally {
        if (!cancelled) setRestoring(false);
      }
    })();

    return () => {
      cancelled = true;
    };
  }, [setCurrentUser]);

  return restoring;
}

// ---------------------------------------------------------------------------
// Route guards — keyed on token presence, not the in-memory user object
// ---------------------------------------------------------------------------

/** Redirects to /login when no access token is persisted. */
function RequireAuth({ children }: { children: ReactNode }) {
  const isAuthenticated = useAtomValue(isAuthenticatedAtom);
  if (!isAuthenticated) {
    return <Navigate to="/login" replace />;
  }
  return children;
}

/** Redirects already-authenticated users away from the login screen. */
function RedirectIfAuthed({ children }: { children: ReactNode }) {
  const isAuthenticated = useAtomValue(isAuthenticatedAtom);
  if (isAuthenticated) {
    return <Navigate to="/" replace />;
  }
  return children;
}

// ---------------------------------------------------------------------------
// App
// ---------------------------------------------------------------------------

function App() {
  const restoring = useRestoreSession();

  if (restoring) {
    return (
      <div className="auth-shell">
        <div className="card">
          <p className="muted">Restoring session…</p>
        </div>
      </div>
    );
  }

  return (
    <BrowserRouter>
      <Routes>
        <Route
          path="/login"
          element={
            <RedirectIfAuthed>
              <LoginView />
            </RedirectIfAuthed>
          }
        />
        <Route
          path="/"
          element={
            <RequireAuth>
              <DashboardView />
            </RequireAuth>
          }
        />
        <Route path="*" element={<Navigate to="/" replace />} />
      </Routes>
    </BrowserRouter>
  );
}

export default App;