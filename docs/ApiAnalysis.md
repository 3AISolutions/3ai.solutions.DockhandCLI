# Dockhand API analysis and implementation decisions

Phase 1 output for [ProjectDescription.md](ProjectDescription.md). Source: the live spec at
`https://dockermanager.beluggaservices.com/api/docs`, inspected 2026-10-24.

## 1. Specification

| Item | Finding |
|---|---|
| Spec URL | `GET /api/docs` returns the OpenAPI JSON directly (no HTML UI in between). `GET /openapi.json` also works, unauthenticated. |
| Format / version | OpenAPI 3.0.3, `info.title = "Dockhand API"`, `info.version = 1.0.53` |
| Size | 282 paths, 407 operations, 50 tags |
| Snapshot | [api/openapi.json](api/openapi.json) (pretty-printed copy used for implementation) |
| Generation | Auto-generated from SvelteKit route handlers. Request/response schemas are partly inferred, so several responses are documented only as prose or as `{}` objects. |

The product is called **Dockhand** (the original brief says "Dockerhand"). The CLI is named
**DockhandCLI** and the binary is **`dockhand`**.

## 2. Authentication

| Scheme | Details |
|---|---|
| `bearerAuth` | `Authorization: Bearer dh_<43-char base64url>`. User-scoped API token. Only evaluated on `/api/*` when no session cookie is present. **Rate limit: 10 failures per IP → HTTP 429 for 5 minutes.** |
| `cookieAuth` | `dockhand_session` cookie set by `POST /api/auth/login`. |

Relevant operations:

| Operation | Purpose |
|---|---|
| `POST /api/auth/login` `{username, password, mfaToken?, provider?}` | Returns session cookie, or `requiresMfa: true` when a second factor is needed. 401 invalid creds, 403 local login disabled, 429 rate limited. |
| `POST /api/auth/tokens` `{name, expiresAt?, password?}` | Creates an API token. **Requires a session, not a Bearer token.** Plaintext `token` returned once (201). Max 25 tokens per user. |
| `GET /api/auth/tokens`, `DELETE /api/auth/tokens/{id}` | List / revoke own tokens. |
| `POST /api/auth/logout` | Destroys the session. |
| `GET /api/auth/session` (public) | `{authenticated, authEnabled, user?}`. Used to validate a token. |
| `GET /api/auth/providers` (public) | Login providers offered (local, LDAP, OIDC). |

**Decision — `dockhand endpoint auth <alias>`** supports both:
1. **Token import**: paste an existing `dh_…` token (hidden prompt or `--token-stdin`).
2. **Login**: username/password (+ MFA code if `requiresMfa`) → session cookie → `POST /api/auth/tokens`
   with the password → store the returned token → `POST /api/auth/logout`. The password is never stored.
   OIDC/passkey logins are browser flows and are **not supported** from the CLI; those users import a token.

After storing, validate with `GET /api/auth/session` (`authenticated == true`).
Because of the 10-failure rate limit, the CLI never retries a 401.

## 3. Environments

| Operation | Notes |
|---|---|
| `GET /api/environments` | Array of `{id:int, name, connectionType, host?, port?, protocol?, icon?, publicIp?, timezone?, hasTlsKey, hasHawserToken}` |
| `GET /api/environments/{id}` | Single environment |
| `POST /api/environments/{id}/test` | Test connectivity to the Docker/Hawser endpoint of an environment |

Every container/image/stack call selects the environment with the **`env` query parameter (integer id)**.
The API documents **no default environment**. Resolution order in the CLI:
`--env` → endpoint's saved default → the only environment if exactly one exists → error asking for `--env`.

## 4. Update workflow

| Operation | Purpose |
|---|---|
| `POST /api/containers/check-updates?env=` | Fresh check. Returns SSE (`progress` events `{checked,total}`, final `result` `{total, updatesFound, results}`), or plain JSON of the final result when sent `Accept: application/json`. **The item schema of `results` is not documented.** |
| `GET /api/containers/check-updates?env=` | Cached pending records: `{environmentId, pendingUpdates:[{containerId, containerName, currentImage, checkedAt?}]}` |
| `GET /api/containers/pending-updates?env=` | `{environmentId, minimumReleaseAgeHours, pendingUpdates:[{containerId, containerName, currentImage, checkedAt, hasImageUpdate, pinnedToDigest, newerVersion?, releaseAgeRemainingHours?}]}` |
| `DELETE /api/containers/pending-updates?env=&containerId=` | Clear pending records (not exposed in v1 unless needed) |
| **`POST /api/containers/batch-update?env=`** `{containerIds:[…]}` | **Chosen apply operation.** "Recreate a set of containers with their latest images, preserving all settings." Returns `{success, results:[{containerId, containerName, success, error?, warnings?}], summary:{total, success, failed}}`. |
| `POST /api/containers/batch-update-stream?env=` `{containerIds, vulnerabilityCriteria?}` | Same, with SSE per-container progress. Used for progress display in interactive terminals. |
| `POST /api/containers/{id}/update` | Recreate with full create-options. **Not used**: needs the whole container config in the body. |

CLI mapping:

| Command | API |
|---|---|
| `check updates` | `POST check-updates` (SSE progress when interactive, JSON otherwise) |
| `check updates --cached` | `GET check-updates` |
| `check updates --pending` | `GET pending-updates` |
| `check updates --all-envs` | the above per environment from `GET /api/environments`, grouped by server/env, partial failures reported per env |
| `check apply update docker <container>` / `update apply <container>` | resolve name → id via `GET /api/containers?env=`, confirm, then `batch-update` with one id |

Apply rules: exact name or id match only (no fuzzy / prefix guessing; ambiguous → error listing candidates);
`--dry-run` sends no mutating request; confirmation unless `--yes`; success reported only from the
per-container `success` flag in the response; no automatic retry. "Preserving all settings" is the API's
claim — the CLI repeats it as the API's statement, not as a guarantee.

**Display limitation:** "available image" can only be shown if the undocumented `results` items (fresh check)
or `newerVersion` (pending) contain it. The CLI will render known fields and pass unknown JSON through in
`--output json`.

## 5. Other v1 command groups

| Command | API |
|---|---|
| `containers list [--all]` | `GET /api/containers?env=&all=` → `[{id, name, image, state, status}]` |
| `containers inspect <c>` | `GET /api/containers/{id}?env=` (raw Docker inspect) |
| `containers logs <c> [--tail N]` | `GET /api/containers/{id}/logs?env=&tail=&since=&until=` |
| `containers start/stop/restart/pause/unpause <c>` | `POST /api/containers/{id}/{action}?env=` (stop/pause/restart confirmed) |
| `images list` | `GET /api/images?env=` (item schema loosely documented) |
| `stacks list` / `stacks show <name>` | `GET /api/stacks?env=`, `GET /api/stacks/{name}/compose?env=` |
| `stacks start/stop <name>` | `POST /api/stacks/{name}/start|stop?env=` → job id; poll `GET /api/jobs/{id}` |
| `stacks restart <name> [--mode]` | `POST /api/stacks/{name}/restart?env=&mode=` (SSE job stream) |
| `stacks deploy <name> [--pull --build --force-recreate]` | `POST /api/stacks/{name}/deploy?env=` (SSE job stream) |
| `stacks down <name> [--remove-volumes]` | `POST /api/stacks/{name}/down?env=` → job id (confirmed; `--remove-volumes` double-confirmed) |
| `jobs show <id>` / `jobs cancel <id>` | `GET` / `DELETE /api/jobs/{id}` |
| `health` | `GET /api/health` (public), `GET /api/health/database` |
| `system [--env]` | `GET /api/system?env=` |
| `endpoint test <alias>` | `GET /api/health` + `GET /api/auth/session` |

## 6. Known API limitations

- No endpoint lists jobs; only `GET`/`DELETE /api/jobs/{id}`. `jobs list` is unsupported.
- No documented default environment.
- `check-updates` POST result item schema, `newerVersion`, image list items and stack list items are
  untyped in the spec; models will tolerate extra/missing fields.
- Token creation needs a cookie session; Bearer-authenticated callers cannot mint tokens.
- OIDC and passkey logins are browser-only.

## 7. Implementation decisions (agreed 2026-10-24)

| Topic | Decision |
|---|---|
| Naming | Solution/projects `DockhandCLI.*`, binary `dockhand`, shorthand `dockhand -srv01 …` |
| Apply operation | `POST /api/containers/batch-update` (stream variant for progress) |
| Auth | Token import **and** username/password login that mints a token |
| Secret storage | Windows Credential Manager, macOS Keychain, Linux libsecret; fallback to a separate secrets file (0600, DPAPI on Windows) with a warning; `DOCKHAND_TOKEN_<ALIAS>` env var override for CI |
| v1 scope | Spec commands + container lifecycle, images/stacks read, stack actions, health/system/jobs |
| Repo | Public `3AISolutions/3ai.solutions.DockhandCLI`, MIT license |
| CI | GitHub Actions build/test on ubuntu/windows/macos; tag-triggered release of self-contained binaries (win-x64, linux-x64, linux-arm64, osx-arm64) |
| Live testing | None — mocked `HttpMessageHandler` tests only |
