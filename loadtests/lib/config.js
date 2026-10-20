// Shared configuration for the PlottR load tests.
//
// Everything is driven by environment variables so the same scripts run locally
// (k6 in Docker against http://localhost:5000) and in CI (k6 on the runner against
// the API started alongside a postgres service container). No secrets are baked in:
// the anonymous-bootstrap admin is created at runtime by the API itself.

/**
 * Base URL of the API under test. Defaults to the CI/local target.
 * Override with: k6 run -e BASE_URL=http://localhost:5000 scenarios/smoke.js
 */
export const BASE_URL = (__ENV.BASE_URL || 'http://localhost:5000').replace(/\/+$/, '');

// ── Anonymous admin bootstrap ───────────────────────────────────────────────
// The API only auto-seeds privileges for the FIRST account created while
// `Auth:AllowAnonymousSignup=true`. In that mode POST /api/Users with no role
// coerces the caller to SystemAdmin (see UserService.CreateAsync). The workflow
// starts the API with AllowAnonymousSignup=true; if this account already exists
// (e.g. a reused local database) setup() falls back to logging in.

export const ADMIN_NAME = __ENV.ADMIN_NAME || 'Load Test Admin';
export const ADMIN_EMAIL = __ENV.ADMIN_EMAIL || 'loadtest@plottr02.local';
export const ADMIN_PASSWORD = __ENV.ADMIN_PASSWORD || 'LoadTest123!';

// ── Upload fixture ──────────────────────────────────────────────────────────
// k6 resolves open() paths relative to the module that calls it (lib/setup.js),
// NOT the process working directory — so this default is '../fixtures/trial.xlsx',
// i.e. loadtests/fixtures/trial.xlsx. `.xlsx` files are gitignored and NOT
// committed, so the workflow generates one at loadtests/fixtures/trial.xlsx with
// loadtests/tools/make-fixture.py before k6 runs.
//
// The generated workbook has the "Subsample Plot Data" sheet with all nine trait
// columns (C..K) empty (plots 101..114, 4 subsamples each), which is exactly what
// an idempotent `overwrite: true` write needs — a non-empty cell would be a no-op
// on the first write.

export const FIXTURE_PATH = __ENV.FIXTURE_PATH || '../fixtures/trial.xlsx';

// ── Auth + API endpoints (verified against the .NET controllers) ────────────
export const ENDPOINTS = {
  health: '/health',
  login: '/api/Auth/login',
  me: '/api/Users/me',
  listTrials: '/api/Trials',
  createTrial: '/api/Trials',
  audit: '/api/Audit',
  trialSheet: (trialId) => `/api/Trials/${trialId}/sheet`,
  trialCells: (trialId) => `/api/Trials/${trialId}/cells`,
};

// ── Thresholds ──────────────────────────────────────────────────────────────
// Smoke is a gate: it must pass hard. Steady is a soak/observation run: looser.

export const SMOKE_THRESHOLDS = {
  http_req_failed: ['rate<0.01'],
  http_req_duration: ['p(95)<1000'],
};

export const STEADY_THRESHOLDS = {
  http_req_failed: ['rate<0.05'],
  http_req_duration: ['p(95)<2000'],
};

// ── Executors ───────────────────────────────────────────────────────────────

export const SMOKE_SCENARIO = {
  smoke: {
    executor: 'constant-vus',
    vus: 1,
    duration: '30s',
  },
};

export const STEADY_SCENARIO = {
  steady: {
    executor: 'ramping-vus',
    startVUs: 0,
    stages: [
      { duration: '30s', target: 20 },
      { duration: '2m', target: 20 },
      { duration: '30s', target: 0 },
    ],
  },
};

// Cell-write coordinates. TraitOrdinal 1..9 only by contract; Value 0..10.
export const TRAIT_ORDINAL_MIN = 1;
export const TRAIT_ORDINAL_MAX = 9;
export const VALUE_MIN = 0;
export const VALUE_MAX = 10;

export function randomInt(min, max) {
  return Math.floor(Math.random() * (max - min + 1)) + min;
}

/**
 * Builds an idempotent cell-write body. `overwrite: true` makes repeated writes
 * to the same cell deterministic (the value is simply re-set), which keeps the
 * load test independent of write ordering across VUs. `Value` is typed as
 * `int?` on the DTO, so a JSON number is correct here.
 */
export function randomCellPayload() {
  return {
    plot: randomInt(101, 114),
    subsample: randomInt(1, 4),
    traitOrdinal: randomInt(TRAIT_ORDINAL_MIN, TRAIT_ORDINAL_MAX),
    value: randomInt(VALUE_MIN, VALUE_MAX),
    overwrite: true,
  };
}
