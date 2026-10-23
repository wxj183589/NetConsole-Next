# Web authentication and LAN contract

## Session model

The first Catalog migration after G2.4 is `schema_v9.sql`. It creates `web_users`, `web_sessions`, and `instance_identity`. Startup idempotently seeds:

| username | initial password | role |
|---|---|---|
| admin | admin | ADMIN |
| superadmin | superadmin | SUPERADMIN |

Passwords are salted PBKDF2-HMAC-SHA256 hashes. A login creates a new random `session_id`, so browsers do not share logout or refresh state. The session cookie is HttpOnly and SameSite=Lax. The CSRF token is stored hashed in the session row and mirrored into a non-HttpOnly cookie only so the same-origin Vue client can send it as `X-NC-CSRF`.

The legacy bearer token is accepted only for compatibility with LocalManager and existing automation. The Vue client never reads the token file or sends a token input.

## API

| Endpoint | Auth | Purpose |
|---|---|---|
| `POST /api/auth/login` | none | Create session; generic failure response with in-memory rate limit |
| `GET /api/auth/me` | session | Return current username and role |
| `GET /api/auth/csrf` | session | Rotate CSRF token |
| `POST /api/auth/logout` | session | Revoke only current session |
| `POST /api/auth/password` | session + CSRF/Origin | Change own password; SUPERADMIN may target a user in the future |
| `/api/system/*` | session or legacy bearer | Existing health, device, task and terminal APIs |

Cookie-authenticated writes require a same-origin `Origin`, matching CSRF header/cookie, and the stored session hash. Bearer-authenticated LocalManager requests retain the existing compatibility path. Integrity quarantine additionally requires `SUPERADMIN`.

## LAN bind

Default remains loopback. LAN is explicit:

```text
NETCONSOLE_NEXT_WEB_BIND=0.0.0.0
# or
NETCONSOLE_NEXT_WEB_LAN=1
```

The port remains `NETCONSOLE_NEXT_WEB_PORT` (default `18771`). In this mode the service accepts the configured port on non-loopback hosts and still rejects other ports. HTTP is intentional for the current single-host/LAN deployment; HTTPS is not silently claimed.

