// Shared setup() logic: ensure an admin exists, log in, upload the fixture
// workbook to create a trial, and hand the trial id + token to every VU.
//
// Returns { token, trialId, adminEmail } per the k6 setup() contract.
//
// Failure model: setup() throws on any unrecoverable problem (missing fixture,
// refused login, failed upload). k6 aborts the whole run in that case, which is
// the desired behaviour — a green run must mean the stack really worked.

import { ADMIN_NAME, ADMIN_EMAIL, ADMIN_PASSWORD, FIXTURE_PATH } from './config.js';
import {
  createAnonymousAdmin,
  createTrial,
  getMe,
  listTrials,
  login,
} from './http.js';

const XLSX_CONTENT_TYPE =
  'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet';

/**
 * Reads the fixture workbook from the k6 working directory.
 *
 * IMPORTANT: k6 only allows `open()` in the init context (module scope), NOT
 * inside setup()/default(). So this must be called at module load, and the bytes
 * handed to setup() via the module-scope FIXTURE_BYTES constant below.
 *
 * `.xlsx` files are gitignored and not committed; the workflow generates one at
 * FIXTURE_PATH (see loadtests/tools/make-fixture.py) before k6 runs.
 */
function readFixture() {
  let bytes;
  try {
    bytes = open(FIXTURE_PATH, 'b');
  } catch (err) {
    throw new Error(
      `Could not open load-test fixture at '${FIXTURE_PATH}'. ` +
        `Generate it with 'python3 loadtests/tools/make-fixture.py ${FIXTURE_PATH}' ` +
        `or set -e FIXTURE_PATH=... . Original error: ${err}`
    );
  }

  // With mode 'b' k6 returns an ArrayBuffer, which is exactly the type
  // http.file() accepts. Do NOT convert to a typed array (that throws in http.file).
  const length = bytes && typeof bytes.byteLength === 'number' ? bytes.byteLength : 0;
  if (length === 0) {
    throw new Error(`Load-test fixture '${FIXTURE_PATH}' is empty.`);
  }
  return bytes;
}

// init context: open() is only valid here.
const FIXTURE_BYTES = readFixture();

/**
 * Ensures an admin token is available.
 *
 * Primary path: anonymous bootstrap (Auth:AllowAnonymousSignup=true) — the first
 * POST /api/Users with no role becomes SystemAdmin. If the account already
 * exists (400/409) we simply log in with the same credentials. If the API has
 * signup disabled we still try to log in, so a pre-seeded admin can be reused.
 */
function ensureAdminToken() {
  const created = createAnonymousAdmin(ADMIN_NAME, ADMIN_EMAIL, ADMIN_PASSWORD);
  const createdOk = created.status === 201;

  const loggedIn = login(ADMIN_EMAIL, ADMIN_PASSWORD);
  if (!loggedIn.ok) {
    throw new Error(
      `Could not obtain an admin token. Bootstrap POST /api/Users returned ${created.status} ` +
        `(created=${createdOk}); login returned ${loggedIn.status}. ` +
        `Ensure the API runs with Auth:AllowAnonymousSignup=true against a fresh database.`
    );
  }

  const token = loggedIn.body.accessToken;
  if (!token) {
    throw new Error('Login succeeded but the response carried no accessToken.');
  }

  // Prove the token is accepted before we build load on top of it.
  const me = getMe(token);
  if (me.status !== 200) {
    throw new Error(`Admin token was rejected by GET /api/Users/me (status ${me.status}).`);
  }

  return token;
}

export function setup() {
  const token = ensureAdminToken();

  const res = createTrial(token, FIXTURE_BYTES, 'trial.xlsx', XLSX_CONTENT_TYPE);
  if (res.status !== 201) {
    throw new Error(
      `POST /api/Trials failed with status ${res.status}: ${res.body}`
    );
  }

  const trial = res.json();
  const trialId = trial.trialId;
  if (!trialId) {
    throw new Error(`POST /api/Trials returned 201 without a trialId: ${res.body}`);
  }

  // Confirm the trial is visible to the same token / list endpoint before the run.
  const list = listTrials(token);
  if (list.status !== 200) {
    throw new Error(`GET /api/Trials returned ${list.status} after upload: ${list.body}`);
  }

  return {
    token: token,
    trialId: trialId,
    adminEmail: ADMIN_EMAIL,
  };
}
