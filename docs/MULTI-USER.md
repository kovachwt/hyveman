# Hyveman — Multi-User Support (users, per-user passkeys, invite links)

**Status:** implemented (2026-08-11) — the plan below is the design record
for the shipped change. Agent wire protocol (`PROTOCOL.md` v1) untouched;
the web API session response changed and shipped in lockstep with the
regenerated frontend client.

## 1. Goal & constraints

**Goal:** Any operator can be a *user*; each user owns their own set of
passkeys. An existing authenticated user can mint a single-use **invite link**
that lets a newcomer self-create their account by registering a first passkey.

**Preserved invariants** (`DESIGN.md` §8, `SECURITY-AUDIT.md`):

- Passkeys remain the *only* login method (no passwords/TOTP/backup codes).
- RP ID / expected origin unchanged → existing passkeys keep working.
- Session = opaque `HttpOnly; Secure; SameSite=Strict` cookie, server-validated,
  sliding 14-day, revocable.
- All state in one data dir; every config/auth mutation audited.
- First-run setup stays trusted-network-gated; the verify step re-checks the
  gate (S8 finding generalizes to invite verify).
- No lockout path: at least one enabled user with a passkey is always
  guaranteed by API guards (self-lockout and last-user blocks), so remote
  recovery stays unnecessary and console `auth reset` remains the only
  fallback.

**Non-goals (v1):** permissions/roles (all users equal), per-user notification
preferences, SSO/SCIM, account recovery beyond admin-issued invite / console
reset.

## 2. Current state

- `passkeys` has no `user_id` — every key belongs to the single admin.
  `WebAuthnService` uses a fixed `AdminUserId = SHA256("hyveman-single-admin")[..16]`
  as the WebAuthn user handle for all keys, and the login
  `IsUserHandleOwnerOfCredentialIdCallback` returns `true` unconditionally.
- `web_sessions` has no `user_id`; `SessionAuthHandler` stamps
  `ClaimTypes.Name = "admin"`; controllers hard-code `Actor() => "admin"`.
- `IsSetupRequiredAsync` = `passkeys.Count == 0`; `SessionResponse.AdminName`
  returns `"admin"`.
- `auth reset` (console) clears `passkeys` + `webauthn_challenges`.
- Established precedents to reuse: `registration_tokens` (hash-only, single-use,
  atomically consumed) and the raw-token-shown-once UX for `reg_` tokens.

## 3. Data model

```text
users(id TEXT PK, name TEXT NOT NULL UNIQUE, display_name TEXT NULL,
      webauthn_user_handle TEXT NOT NULL,        -- base64url 16B
      disabled INTEGER NOT NULL DEFAULT 0,
      created TEXT NOT NULL, created_by TEXT NULL) -- inviting user id, or 'setup'/'console'

invitations(id TEXT PK, token_hash TEXT NOT NULL UNIQUE,
            created_by TEXT NULL REFERENCES users(id),  -- inviting user
            for_user_id TEXT NULL REFERENCES users(id), -- null = new-user invite; set = passkey-reset invite (future)
            created TEXT NOT NULL, expires_at TEXT NULL,
            consumed_at TEXT NULL, revoked INTEGER NOT NULL DEFAULT 0)
```

Column additions:

- `passkeys.user_id TEXT NOT NULL REFERENCES users(id) ON DELETE CASCADE`
  (plus optional `UNIQUE(user_id, name)`).
- `web_sessions.user_id TEXT NOT NULL REFERENCES users(id) ON DELETE CASCADE`.

`passkeys.credential_id` stays globally `UNIQUE` (WebAuthn credential IDs are
globally unique; login resolves the user through the passkey row).

## 4. Migration (V8)

Follow the established drop+rename pattern (`Migrations.cs` V5):

1. `CREATE TABLE users(...)`; insert the bootstrap user
   (`id='usr_admin'`, `name='admin'`, `display_name='Hyveman Administrator'`).
   The bootstrap user's `webauthn_user_handle` is **the existing
   `AdminUserId` constant** so already-enrolled passkeys keep authenticating
   without re-enrollment.
2. Rebuild `passkeys` with `user_id NOT NULL` + FK cascade; backfill existing
   rows to the bootstrap user.
3. Rebuild `web_sessions` with `user_id NOT NULL`; backfill existing rows to
   the bootstrap user (the operator's current session survives the upgrade).
4. `CREATE TABLE invitations(...)`.

## 5. WebAuthn service changes

The WebAuthn **user handle is per-user** (`users.webauthn_user_handle`).

**`BeginRegistrationAsync`** dispatches on context (one method, three modes):

- **Setup** (`users.Count == 0` + trusted network): pending random handle
  stored in the ceremony context; verify creates the first `users` row.
- **Invite acceptance** (valid `inviteToken`, no session): validate the invite
  (exists, not consumed/revoked/expired — S8 gate **here**); store the pending
  handle + invite reference in the ceremony `origin_context`; do **not** create
  the user row yet (the invitee may abandon).
- **Authenticated add**: register for the session user; `user.id` = that user's
  stored handle; `ExcludeCredentials` = that user's existing keys.

**`CompleteRegistrationAsync`** re-validates the relevant gate (S8), verifies
attestation, then **in one transaction**:

- setup → create first `users` row + passkey + (auto) session;
- invite → create `users` row (final name supplied by invitee) + passkey +
  mark `invitations.consumed_at` + (auto) session; **consume only on success**
  (a name-collision 400 must not consume the invite);
- authenticated → insert passkey under the session user.

**Login** is unchanged in UX: `AllowedCredentials` = all passkeys of **enabled**
users; identity resolves from the credential. `CompleteLoginAsync` rejects
disabled users and **strengthens** `IsUserHandleOwnerOfCredentialIdCallback`
(compare the returned user handle to the stored one when present; accept an
absent handle — credentials are non-discoverable today, so `credential_id` is
authoritative).

`IsSetupRequiredAsync` → `users.Count == 0`.

## 6. Session & actor threading

- `SessionAuthHandler`: stamp `ClaimTypes.Name = user.name`,
  `ClaimTypes.NameIdentifier = user.id`; re-check `users.disabled` on every
  request and fail/delete the session when disabled.
- `ISessionStore`: `CreateAsync(now, lifetime, userId, ct)`;
  `WebSession` carries `UserId`; add `RevokeAllForUserAsync(userId)`.
- Controllers' `Actor()` → `User.Identity?.Name` (real username) instead of
  the hard-coded `"admin"`.

## 7. Invite flow

- **Create** (authenticated): `POST /api/v1/users/invitations` → mints
  `inv_<...>` (hash stored, raw returned **once**), optional
  `expiresInMinutes` (≤ 7 days) and `intendedName`. Response includes the
  shareable URL `https://<origin>/accept-invite#token=<raw>` — token in the
  URL **fragment** so it never reaches server logs / `Referer` (parallels the
  `reg_` "never in URL/logs" rule; the fragment is the one exception because
  a link is inherent to invites).
- **List/Revoke** (authenticated): metadata only, never the token.
- **Accept** (unauthenticated): `/accept-invite` route reads the fragment,
  optionally inspects the invite (friendly banner), prompts for username +
  passkey name, runs the registration ceremony with `inviteToken` in the
  body, refetches `/auth/session` and enters the app.

Forward-compatible: `invitations.for_user_id` supports the **passkey-reset
invite** fast-follow (existing user who lost all passkeys) — schema now,
behavior later.

## 8. User management & lifecycle guards

New `UsersController` (`/api/v1/users`, `/api/v1/users/invitations`), all
authenticated, all users equal. Mutations audited.

- List/detail (incl. per-user passkey metadata); disable/enable (disable also
  revokes the user's sessions); delete (cascades passkeys + sessions; audit
  rows keep the denormalized `actor` string).
- Admin may **remove** another user's passkey (lost-device recovery); adding
  always requires that user's personal ceremony (own session or a reset
  invite).
- `/api/v1/auth/passkeys` (list/register/remove) becomes **"My passkeys"**,
  scoped server-side to the session user.

**Self-lockout guards:**

- Disabling/deleting yourself is blocked.
- Disabling/deleting the **last enabled user** is blocked.
- Removing a passkey is blocked when it would leave **zero enabled login
  paths**, and removing your **own last** passkey is blocked even when other
  users exist (self-lockout).

## 9. HTTP endpoints

| Method | Path | Auth | Notes |
|---|---|---|---|
| `GET` | `/api/v1/auth/session` | anon | `SessionResponse` gains `user{id,name,displayName}`; `setupRequired` = users-empty; `adminName` removed (lockstep). |
| `POST` | `/api/v1/auth/passkeys/register/options` | anon/session | body gains optional `inviteToken`. |
| `POST` | `/api/v1/auth/passkeys/register/verify` | anon/session | body envelope `{response, inviteToken?, username?, displayName?}`; auto-session for setup/invite. |
| `POST` | `/api/v1/auth/invitations/inspect` | anon | validates token, returns who/when, does **not** consume. |
| `GET/POST` | `/api/v1/users`, `/api/v1/users/{id}` | session | list/detail. |
| `POST` | `/api/v1/users/{id}/disable`, `.../enable` | session | lifecycle; disable revokes sessions. |
| `DELETE` | `/api/v1/users/{id}`, `/api/v1/users/{id}/passkeys/{passkeyId}` | session | destructive (confirmed by frontend); per-user passkey remove. |
| `POST` | `/api/v1/users/invitations`, `.../{id}/revoke`, `GET /api/v1/users/invitations` | session | invite create/list/revoke; raw token only on create. |

CSRF + rate-limit wiring already covers these; no new exemptions.

**Web API compatibility:** removing `adminName` / adding `user` is a breaking
change within `/api/v1`; ship API + regenerated frontend together (same
release).

## 10. Frontend changes

- Regenerate `src/api/generated/` from the updated OpenAPI (Orval).
- `AuthProvider` exposes `session.user` (`{id,name,displayName}`); guards
  unchanged (`authenticated` still means a valid session).
- New **public** route `/accept-invite` (`pages/AcceptInvitePage.tsx`, modeled
  on `SetupPage`): token from `location.hash`, optional inspect banner,
  username + passkey-name form, ceremony with `inviteToken`, session refetch.
- `auth/passkey.ts`: forward `inviteToken` + `username` on registration.
- New admin page `/admin/users` (`features/users/UsersPage.tsx`): users table
  (disable/enable/delete with self- and last-user guards), invite creation
  showing the raw invite URL once (mirror of the `reg_` token UX), pending
  invites with revoke, per-user passkey list (remove only).
- Rename "Passkeys" → **"My passkeys"** (`/admin/passkeys`), scoped to the
  session user by the API.
- `AppShell`: "Users" nav item; show current user's display name.

## 11. Security considerations

- Invite tokens: URL **fragment** only; sent in JSON bodies; never query
  strings, logs, or `Referer`; raw shown once.
- Invite re-validated at verify before the commit (S8 generalization).
- Setup gate now `users.Count == 0` + trusted network; `auth reset` clears
  users too.
- Disabled/deleted users: sessions revoked immediately; handler re-checks on
  each request.
- Per-user WebAuthn handles; the blanket-`true` login callback is closed.
- Rate limiting / CSRF / Origin checks unchanged.

## 12. Tests

**API:** migration backfill (existing DB → bootstrap user owns passkeys and
sessions; live cookie still authenticates); setup creates first user; invite
create/list/revoke; acceptance creates user + passkey + consumes invite;
double-spend / expired / revoked rejected at verify; name-collision does not
consume; login resolves the correct user; disabled user rejected and sessions
invalidated; self/last-user/last-passkey guards; audit actor = real username.

**Web:** accept-invite route (fragment token, success → app, invalid → clear
error); UsersPage (token shown once, revoke/disable/delete confirmations,
guards surfaced); My-passkeys scoped; AuthProvider exposes `session.user`.

**Playwright:** invite → accept → app as new user; disable kicks sessions;
last-user self-delete blocked.

## 13. Docs to update

- `DESIGN.md` §8, §5.1, §13 decisions log.
- `API.md` §7/§8 (+ §15 compat note).
- `FRONTEND.md` §5/§7/§8.6.
- `SECURITY-AUDIT.md` (callback fix, S8 generalization).
- `README.md`, `INSTALL.md`.

## 14. Compatibility

- Existing installs: single admin → `users.admin`; passkeys and current
  session keep working (bootstrap handle = old constant; sessions backfilled).
  No forced re-enrollment or logout.
- Agent protocol untouched → no protocol version bump.
- Breaking web-API change shipped lockstep with the frontend.

## 15. Implementation order

1. `Migrations.cs` V8 (tables + backfill).
2. `IUserStore`/`IInvitationStore` + SQLite impls; `user_id` plumbing through
   `IPasskeyStore`/`ISessionStore`.
3. `WebAuthnService` per-user handles, 3-mode registration, invite consume
   transaction, strengthened login, users-based setup gate.
4. `SessionAuthHandler` user claims + disabled re-check; `Actor()` uses the
   real username.
5. `AuthController` invite-aware register + `UsersController` + invitations
   endpoints.
6. Frontend: regenerate client → AuthProvider user, `/accept-invite`,
   `/admin/users`, My-passkeys, AppShell.
7. Tests (API + web + e2e); docs updates.

## 16. Out of scope / fast-follow

- Per-user roles/permissions; per-user notification prefs / audit filters.
- **Passkey-reset invite** (existing user, lost all keys) — `for_user_id`
  schema prepared; behavior is a quick follow.
- Username-scoped login; discoverable/resident-key requirement.
- Console additions (`auth list-users`, `auth disable-user`) beyond extending
  `auth reset`.
