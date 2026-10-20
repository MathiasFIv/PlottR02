// Thin HTTP helpers around the API. Kept dependency-free (k6's own http module)
// so the same module works in the k6 binary and the grafana/k6 Docker image.
//
// Endpoint shapes here were verified against the .NET controllers:
//   AuthController  POST /api/Auth/login  -> { user, accessToken, refreshToken }
//   UsersController POST /api/Users       -> 201 { id, name, email, role }
//   TrialsController POST /api/Trials     -> 201 TrialSummary { trialId, ... }
//   TrialsController GET  /api/Trials/{id}/sheet
//   TrialsController POST /api/Trials/{id}/cells
//   Program.cs      GET  /health          -> 200 empty

import http from 'k6/http';
import { BASE_URL, ENDPOINTS } from './config.js';

const JSON_HEADERS = { 'Content-Type': 'application/json' };

export function url(path) {
  return `${BASE_URL}${path}`;
}

function authHeaders(token) {
  return Object.assign({}, JSON_HEADERS, {
    Authorization: `Bearer ${token}`,
  });
}

/**
 * POST /api/Auth/login
 * Request:  { "email": string, "password": string }
 * Response: { "user": { id, name, email, role }, "accessToken": string, "refreshToken": string }
 */
export function login(email, password) {
  const res = http.post(
    url(ENDPOINTS.login),
    JSON.stringify({ email, password }),
    { headers: JSON_HEADERS, tags: { name: 'POST /api/Auth/login' } }
  );

  if (res.status !== 200) {
    return { ok: false, status: res.status, body: res.body };
  }

  return { ok: true, status: res.status, body: res.json() };
}

/**
 * POST /api/Users (anonymous bootstrap, no Authorization header).
 * Request:  { "name": string, "email": string, "password": string }
 *           (omit `role` so the first account is coerced to SystemAdmin)
 * Response: 201 { id, name, email, role }
 */
export function createAnonymousAdmin(name, email, password) {
  const res = http.post(
    url('/api/Users'),
    JSON.stringify({ name: name, email: email, password: password }),
    {
      headers: JSON_HEADERS,
      tags: { name: 'POST /api/Users' },
      // 409 = the account already exists (e.g. a reused database). Treat it as an
      // expected response so it does not count towards http_req_failed.
      responseCallback: http.expectedStatuses(201, 409),
    }
  );

  return { status: res.status, body: res.body };
}

/** GET /api/Users/me — used to sanity-check that a token is accepted. */
export function getMe(token) {
  return http.get(url(ENDPOINTS.me), {
    headers: authHeaders(token),
    tags: { name: 'GET /api/Users/me' },
  });
}

/** GET /api/Trials */
export function listTrials(token) {
  return http.get(url(ENDPOINTS.listTrials), {
    headers: authHeaders(token),
    tags: { name: 'GET /api/Trials' },
  });
}

/** GET /api/Trials/{id}/sheet */
export function getSheet(token, trialId) {
  return http.get(url(ENDPOINTS.trialSheet(trialId)), {
    headers: authHeaders(token),
    tags: { name: 'GET /api/Trials/{id}/sheet' },
  });
}

/** POST /api/Trials/{id}/cells (JSON) */
export function editCell(token, trialId, payload) {
  return http.post(url(ENDPOINTS.trialCells(trialId)), JSON.stringify(payload), {
    headers: authHeaders(token),
    tags: { name: 'POST /api/Trials/{id}/cells' },
  });
}

/**
 * POST /api/Trials (multipart/form-data)
 * The controller binds the upload to a plain `IFormFile file` parameter, so the
 * multipart field name MUST be exactly "file" (no [FromForm] name override).
 * Requires a SystemAdmin / Researcher / TrialManager token.
 */
export function createTrial(token, fileBytes, fileName, contentType) {
  const form = {
    // multipart value name == parameter name == "file"
    file: http.file(fileBytes, fileName, contentType),
  };

  return http.post(url(ENDPOINTS.createTrial), form, {
    headers: { Authorization: `Bearer ${token}` },
    tags: { name: 'POST /api/Trials' },
  });
}
