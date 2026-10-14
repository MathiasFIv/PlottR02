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
import badgeLight from './Assets/plottR-badge-light-small.png';
import badgeDark from './Assets/plottR-badge-dark-small.png';
import { currentUserAtom, isAuthenticatedAtom } from './store/atoms';
import { LoginView } from './views/LoginView';
import { DashboardView } from './views/DashboardView';
import { RecordView } from './views/RecordView';
import { SheetView } from './views/SheetView';

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
    return <Navigate to="/record" replace />;
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
      <div className="min-h-screen flex items-center justify-center p-6">
        <div className="card card-border bg-base-100 shadow-sm p-6">
          <img
            src={badgeLight}
            alt="PlottR 0.2"
            className="block dark:hidden w-14 h-14 mx-auto mb-3"
          />
          <img
            src={badgeDark}
            alt="PlottR 0.2"
            className="hidden dark:block w-14 h-14 mx-auto mb-3"
          />
          <p className="text-sm text-base-content/70">Restoring session…</p>
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
        <Route
          path="/record"
          element={
            <RequireAuth>
              <RecordView />
            </RequireAuth>
          }
        />
        {/* Read-only ratings sheet for one trial (N2) — the full-region host
            for the same SheetGrid embedded in RecordView. */}
        <Route
          path="/trials/:trialId/sheet"
          element={
            <RequireAuth>
              <SheetView />
            </RequireAuth>
          }
        />
        {/* Landing page TBD — everything currently funnels to the record view. */}
        <Route path="*" element={<Navigate to="/record" replace />} />
      </Routes>
    </BrowserRouter>
  );
}

export default App;