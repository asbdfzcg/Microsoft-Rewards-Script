# Control API (middleman)

A small, dependency-free HTTP service that wraps the Microsoft Rewards Script so
a dashboard (or any tool) can drive it over HTTP:

- **Control** – start / stop / restart a run, and shut the API down remotely.
- **Live logs** – stream the bot's output over Server-Sent Events (SSE).
- **Live points** – points update _while the run is happening_, not just at the end.
- **Errors** – warnings, errors and per-account failures, parsed from the log stream.
- **Accounts** – a local overview of configured accounts with full emails but no credentials or secret values.
- **Diagnostics** – browse the bot's error captures (screenshot / html / error text).
- **Config editing** – read and (optionally) update `config.json`, validated by the bot's own schema.

## It writes nothing

This API is **stateless**. It keeps everything in memory and creates no folders
and no files — no database, no `data/`, no JSON, not even a PID file. The bot
repo stays exactly as clean as you left it.

Anything worth keeping (point history, run history, charts, the schedule) is
stored by the **dashboard**, in the dashboard's own `data/` folder. That's the
side that needs the data, so that's the side that owns it.

The one and only file this API can ever touch is `config.json` itself, and only
if you explicitly opt in with `API_ALLOW_CONFIG_WRITE=true` and then save a
config from the dashboard (it keeps a `config.json.bak` when it does).

It also does **not** modify the bot. It spawns the exact command you'd run by
hand (`node dist/index.js` by default) as a child process and watches its
output. It only launches, kills and restarts it — nothing more.

```
  bot repo                                    dashboard repo
  ┌───────────────────────────────┐           ┌──────────────────────┐
  │ node scripts/api/server.js    │           │ rewards-dashboard    │
  │   ├── spawns → the bot        │ ◀──HTTP── │  CONTROL_API_URL     │
  │   └── API_TOKEN=<secret>      │   + token │  CONTROL_API_TOKEN   │
  │   (stateless, writes nothing) │           │  data/  ← all state  │
  └───────────────────────────────┘           └──────────────────────┘
```

- No npm dependencies – Node built-ins only.
- No changes to `src/`. Everything lives under `scripts/api/`.
- Same ESM `.js` convention as the other `scripts/` helpers (Node ≥ 24).
- **Platform-agnostic** – pure Node, so it runs the same on Windows, macOS,
  Linux and in Docker (process handling uses `taskkill` on Windows and process
  groups elsewhere).

## Run it

```bash
# 1. Build the bot once (the API launches the compiled entrypoint by default)
npm run build

# 2. Start the control API
npm run api          # or: node scripts/api/server.js
```

`GET http://127.0.0.1:3010/` returns a JSON index of the endpoints — a quick way
to confirm it's up. There is no web page here; point the dashboard at it instead.

By default it binds to `127.0.0.1` with **no token**. Fine for a local poke; set
`API_TOKEN` as soon as anything else needs to reach it.

## Security model (keep it simple)

Local-first, single-user, so: **one shared token**.

- Set `API_TOKEN` to any secret string and give the **same** value to the
  dashboard (as its `CONTROL_API_TOKEN`). Every endpoint except `/health` and
  the `/` index then requires it — via `Authorization: Bearer <token>`,
  `X-API-Key: <token>`, or `?token=<token>` (the query form exists because
  `EventSource` can't set headers). Tokens are compared in constant time.
- No token → the API is open. Fine on loopback, risky if exposed (it can start
  processes on your machine), and it warns loudly at startup — as an error if
  it's bound to a non-loopback address.
- `/health` is public on purpose: it lets the dashboard tell "API is down" apart
  from "API is up but rejected my token".
- Going beyond localhost? Put it behind a reverse proxy (Caddy/nginx/Traefik)
  and let that terminate TLS.
- `/config` redacts webhook URLs/tokens by default. Account passwords, recovery addresses, TOTP secrets, and proxy credentials are never
  exposed. The local account overview does return the full email address.

Generate a token:

```bash
node -e "console.log(require('crypto').randomBytes(32).toString('hex'))"
```

## Configuration (environment variables)

All optional. They can live in your existing `.env` (the API loads it from the
repo root automatically).

| Variable                  | Default              | Purpose                                                                                           |
| ------------------------- | -------------------- | ------------------------------------------------------------------------------------------------- |
| `API_HOST`                | `127.0.0.1`          | Interface to bind. `0.0.0.0` to expose (set a token!).                                            |
| `API_PORT`                | `3010`               | Listen port.                                                                                      |
| `API_TOKEN`               | _(unset)_            | Shared token. If set, everything except `/health` and `/` requires it.                            |
| `API_CORS_ORIGIN`         | `*`                  | `Access-Control-Allow-Origin`. Only matters if a browser calls this API directly.                 |
| `API_LOG_BUFFER`          | `2000`               | Log lines kept in memory (ring buffer) for replay.                                                |
| `API_RUN_HISTORY`         | `20`                 | Completed runs kept in memory for `/history`. The durable copy lives in the dashboard's database. |
| `API_STOP_TIMEOUT_MS`     | `15000`              | Grace period after SIGTERM before escalating to SIGKILL.                                          |
| `API_RUN_COMMAND`         | _(auto)_             | Override the launch binary. Auto-detects `dist/index.js`, else `npm run ts-start`.                |
| `API_RUN_ARGS`            | _(none)_             | Args for `API_RUN_COMMAND`. Space-separated or a JSON array.                                      |
| `API_DIAGNOSTICS_DIR`     | `<repo>/diagnostics` | Where the bot writes error captures (matches its default). Read-only to this API.                 |
| `API_ALLOW_CONFIG_WRITE`  | `false`              | Allow `PUT`/`PATCH /config` to edit `config.json`.                                                |
| `API_ALLOW_ENV_OVERRIDES` | `false`              | Allow `POST /start` to inject env vars into the child.                                            |
| `API_ALLOW_CONFIG_REVEAL` | `false`              | Allow `GET /config?reveal=1` (with token) to return unredacted secrets.                           |
| `API_VALIDATOR_MODULE`    | _(auto)_             | Path to the compiled config validator (defaults to `dist/util/Validator.js`).                     |

CLI flags `-port`, `-host`, `-token` are also accepted and override the env:

```bash
node scripts/api/server.js -host 0.0.0.0 -port 3010 -token "$MY_TOKEN"
```

## Endpoints

Base URL: `http://<host>:<port>`

### Read

| Method | Path                      | Description                                                                    |
| ------ | ------------------------- | ------------------------------------------------------------------------------ |
| `GET`  | `/`                       | Public JSON index: name, version, `authRequired`, endpoint list.               |
| `GET`  | `/health`                 | Liveness. Public. `{ ok, name, version, state, uptimeSec, authRequired }`.     |
| `GET`  | `/status`                 | Full run + process state, including the live points summary.                   |
| `GET`  | `/points`                 | **Live points** — see below.                                                   |
| `GET`  | `/logs`                   | Buffered logs. Query: `limit`, `level` (min level), `afterId`.                 |
| `GET`  | `/errors`                 | Recent warnings/errors + per-account errors. Query: `limit`, `warnings=false`. |
| `GET`  | `/history`                | Runs this API has launched since it started (in memory, newest first).         |
| `GET`  | `/accounts`               | Account overview from `.env`; full email, no passwords/TOTP/recovery values.                                           |
| `GET`  | `/diagnostics`            | List error captures (name, time, which artifacts exist, first error line).     |
| `GET`  | `/diagnostics/<n>/<file>` | One artifact: `screenshot.png`, `error.txt`, or `dump.html`.                   |
| `GET`  | `/config`                 | `config.json`, secrets redacted. `?reveal=1` if enabled + authed.              |
| `GET`  | `/events`                 | **SSE stream** of logs + status. See below.                                    |

### Control & write

| Method  | Path        | Body                      | Description                                                                       |
| ------- | ----------- | ------------------------- | --------------------------------------------------------------------------------- |
| `POST`  | `/start`    | `{ accountIndex?, args?, env? }` | Launch all accounts, or only one `.env` slot such as `{ "accountIndex": 2 }`. `409` if active. |
| `POST`  | `/stop`     | `{ force? }`              | SIGTERM (or SIGKILL if `force`), escalates after the grace window. `409` if idle. |
| `POST`  | `/restart`  | `{ force?, accountIndex?, args?, env? }` | Stop (if running), then launch all accounts or one selected slot. |
| `POST`  | `/shutdown` | `{ force? }`              | Stop the bot (if running) and exit the API process.                               |
| `PUT`   | `/config`   | _(full config)_           | Replace `config.json`. Validated. Needs `API_ALLOW_CONFIG_WRITE`.                 |
| `PATCH` | `/config`   | _(partial)_               | Deep-merge into `config.json`. Validated. Needs `API_ALLOW_CONFIG_WRITE`.         |

`env` in a `/start` body is ignored unless `API_ALLOW_ENV_OVERRIDES=true`, and a
few launch-hijacking keys (`NODE_OPTIONS`, `LD_PRELOAD`, …) are always dropped.

> **Scheduling lives in the dashboard.** A cron here would have to persist a
> schedule file into the bot repo, so it moved to the dashboard, which already
> has a database. It arms its own timer and simply calls `POST /start`.

## Live points

`GET /points` reflects the run **as it happens**. The bot prints its balance and
every gain as it earns them (`SEARCH-BING`, `READ-TO-EARN`, `DAILY-CHECK-IN`,
`CLAIM-REWARD`, …), and the API folds those lines into a running tally — so you
can watch points tick up mid-run instead of waiting for the final total.

```jsonc
{
    "state": "running",
    "live": true,
    "currentAccount": "you@example.com",
    "balance": 12480, // that account's balance right now
    "collected": 155, // earned so far this run, all accounts
    "updatedAt": "…",
    "accounts": [
        {
            "email": "you@example.com",
            "collected": 155, // live while working; the exact total once done
            "balance": 12480,
            "bySource": { "search": 120, "read": 30, "checkIn": 5 },
            "done": false,
            "success": null,
            "error": null
        }
    ],
    "totals": null, // filled in from the bot's own RUN-END line
    "finished": false
}
```

When an account finishes, its live tally is replaced by the authoritative
`ACCOUNT-END` numbers, so the running estimate can never drift from the truth.

## Live log stream (SSE)

`GET /events` emits `text/event-stream`. Events:

- `hello` – a `/status` snapshot on connect.
- `log` – one structured log line (each carries an SSE `id`).
- `status` – emitted on every state change / run milestone (including exits).

On connect it replays the last `?replay=N` lines (default 100). On reconnect,
browsers send `Last-Event-ID` automatically and the server replays only newer
lines. A `: ping` keep-alive is sent every 15s.

`EventSource` can't send an `Authorization` header, so pass the token as a query
param when auth is on: `/events?token=<API_TOKEN>`.

```js
const es = new EventSource('http://127.0.0.1:3010/events?replay=100&token=…')
es.addEventListener('log', e => console.log(JSON.parse(e.data).message))
es.addEventListener('status', e => render(JSON.parse(e.data)))
```

### Structured log entry

```jsonc
{
    "id": 42,
    "receivedAt": "2026-07-10T03:26:24.206Z",
    "ts": "7/10/2026, 3:26:24 AM", // bot's own timestamp (null for shell/npm lines)
    "level": "info", // info | warn | error | debug
    "platform": "MAIN", // MAIN | MOBILE | DESKTOP | null
    "title": "ACCOUNT-END", // the bot's log title, or null
    "message": "Completed account: …",
    "raw": "[7/10/2026, 3:26:24 AM] […", // the verbatim line
    "source": "stdout", // stdout | stderr | controller
    "parsed": true // matched the bot's structured log format
}
```

## Examples

```bash
TOKEN="Authorization: Bearer $API_TOKEN"

curl -s localhost:3010/health
curl -s -H "$TOKEN" localhost:3010/status
curl -s -H "$TOKEN" -X POST localhost:3010/start -d '{}'

# Run only ACCOUNT_2 from the existing .env (credentials never leave the API)
curl -s -H "$TOKEN" -H 'Content-Type: application/json' \
  -X POST localhost:3010/start -d '{"accountIndex":2}'

# Watch points climb during a run
watch -n2 "curl -s -H '$TOKEN' localhost:3010/points"

# Toggle one worker (needs API_ALLOW_CONFIG_WRITE=true)
curl -s -H "$TOKEN" -X PATCH localhost:3010/config -d '{"workers":{"doMobileSearch":false}}'

# Live logs from the terminal
curl -sN -H "$TOKEN" "localhost:3010/events?replay=50"
```

## Keeping it running

A long-lived process — run it like any other service.

- **Terminal / dev:** `npm run api`
- **pm2:** `pm2 start scripts/api/server.js --name mrs-api`
- **systemd:** `ExecStart=/usr/bin/node /opt/microsoft-rewards-script/scripts/api/server.js`
  with `EnvironmentFile=/opt/microsoft-rewards-script/.env`
- **Docker:** copy `scripts/` into the image, run `node scripts/api/server.js` as
  a long-lived process, publish `API_PORT`, and set `API_HOST=0.0.0.0` (otherwise
  it binds to loopback _inside_ the container and nothing can reach it).

Once listening it writes one machine-readable line to stdout:
`__API_READY__ {"host","port","pid","version","auth"}` — wait for that instead of
guessing a startup delay. If the port is already taken it exits immediately
rather than silently double-spawning the bot.

## Files

| File                | Role                                                                              |
| ------------------- | --------------------------------------------------------------------------------- |
| `server.js`         | HTTP server: routing, SSE, auth, CORS. JSON only — no UI, no disk.                |
| `processManager.js` | Child-process lifecycle + log ingestion + run state. Emits `log`/`status`/`exit`. |
| `logParser.js`      | Pure parser: raw line → structured entry, plus the live points accumulator.       |
| `accounts.js`       | Local account overview plus safe single-account child-environment selection.     |
| `configEditor.js`   | Validate (using the bot's own schema) + atomic write for `config.json`.           |
| `lib.js`            | Tiny helpers (env, config load, secret redaction). No external deps.              |
