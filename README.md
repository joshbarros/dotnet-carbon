# dotnet-carbon

Carbon credit batches and the purchases that draw them down. The scarce number is the **tonnes left** in a batch (one credit is one tonne of CO2e), and the **Batches** module is its only writer: it holds tonnes with a single conditional `UPDATE` that matches only while the tonnes left still cover the purchase, so when two buyers fight for the last tonne, one commits a hold and the other is told the batch is short and no hold is written. A beneficiary reference that contains the poison token `POISON` is committed as rejected by the Certificates module, the message is dead-lettered, and a compensating command gives the tonnes back. A later reversal also gives the tonnes back to the batch, but it does not roll back the allocation certificate that was already issued.

One ASP.NET Core process, one Postgres database. An HTTP write saves the purchase and an outbox row in one local transaction; a `BackgroundService` relay publishes outbox rows to RabbitMQ (the work queue) and Kafka (the event log); the next module consumes from RabbitMQ, claims an inbox key in the same transaction as its own write, and writes its own row and outbox row. Saga steps never call each other in-process. Delivery is at-least-once: the relay can publish and crash before it marks a row sent, so the row goes out again, and every consumer dedupes with its inbox key. Each saga step only moves a purchase forward from the status it expects.

Everything behind the saga is signed in: **ASP.NET Core Identity** with **JWT access tokens and rotating refresh tokens** (see [Authentication and authorization](#authentication-and-authorization)). Customers see and reverse only their own purchases; staff see everything and open batches.

> The demo credentials in `compose.yaml` and `appsettings.Development.json` are for local use only. Set `JWT_KEY` and `ADMIN_PASSWORD` (see `.env.example`) before putting this anywhere shared. Compose binds every port to `127.0.0.1`.

![The carbon UI signed in as the administrator, after two purchases fought for the last tonnes: one purchase allocated, one short](docs/screenshot.png)

*The UI reads everything from the API; shown signed in as the seeded administrator, on the batch where two purchases fought for the last tonnes: one purchase was allocated, the other was told it is short.*

## Purchase statuses

```mermaid
flowchart LR
    Requested -->|tonnes held| Held
    Requested -->|short: no hold written| Rejected
    Held -->|certificate issued| Allocated
    Held -->|poison reference: certificate rejected| Rejected
    Held -->|reversal| Reversed
    Allocated -->|reversal| Reversed
```

`Reversed` and `Rejected` (after a hold) give the tonnes back through a `ReleaseTonnes` command, and Batches releases a hold at most once. The certificate has its own status (`Issued` or `Rejected`) and a reversal never changes it. If the reversal gets there before the final mark, the late mark finds the purchase is no longer `Held` and does nothing.

## How the modules talk

```mermaid
sequenceDiagram
    participant C as Client
    participant P as Purchases
    participant B as Batches
    participant X as Certificates

    C->>P: POST /api/purchases
    Note over P: purchase + outbox row, one transaction
    P-->>C: 202 Requested
    P->>B: tonnes.reserve (relay → RabbitMQ, copy to Kafka)
    Note over B: inbox claim, conditional UPDATE, hold, outbox row
    alt tonnes left cover the purchase
        B->>X: tonnes.held
        B->>P: tonnes.held (Requested → Held)
        alt reference is clean
            Note over X: certificate, outbox row
            X->>P: certificate.issued (Held → Allocated)
        else reference contains POISON
            Note over X: committed Rejected, then the message is dead-lettered
            X->>B: tonnes.release (compensating command)
            X->>P: certificate.rejected (Held → Rejected)
            B->>P: tonnes.released
        end
    else batch short
        B->>P: tonnes.short (Requested → Rejected, no hold)
    end
    C->>P: POST /api/purchases/{id}/reversal
    P->>B: tonnes.release (Held or Allocated → Reversed)
    B->>P: tonnes.released
```

| Module | Owns | Consumes | Emits |
| --- | --- | --- | --- |
| **Identity** (`libs/identity`) | `identity` schema: users, roles, refresh tokens. | | |
| **Purchases** (`libs/purchases`) | `Purchases`: status, tonnes, total, beneficiary reference. Validation, the poison rule, the total. | `tonnes.held`, `tonnes.short`, `certificate.issued`, `certificate.rejected`, `tonnes.released` | `tonnes.reserve`, `tonnes.release` (reversal) |
| **Batches** (`libs/batches`) | `Batches.TonnesLeft` (sole writer after the batch opens) and `TonneHolds`. | `tonnes.reserve`, `tonnes.release` (prefetch 1) | `tonnes.held`, `tonnes.short`, `tonnes.released` |
| **Certificates** (`libs/certificates`) | `Certificates`. | `tonnes.held` | `certificate.issued`, `certificate.rejected`, `tonnes.release` (poison) |
| **Shared** (`libs/shared`) | The relay, the consumer loop with retry and dead-letter, the Kafka log, auth primitives, middleware. Each module owns its own `OutboxMessages` and `InboxMessages` tables. | | |

RabbitMQ topology is declared in code (`libs/shared/Messaging/Topology.cs`): exchange `carbon.work`, one queue per module (`batches.commands`, `certificates.work`, `purchases.events`), a `.retry` queue per module (2 s TTL, then back to the main queue, 3 attempts), and a `.dlq` per module behind `carbon.dlx`. A transient failure retries; a poison message goes straight to the dead-letter queue after its rejection is committed. Kafka has one topic, `carbon.events`, one partition, replication factor 1, keyed by purchase id.

## Architecture

One deployable (`apps/api`), several libraries. Each business module is its own class library with its own `DbContext`, its own Postgres **schema** (`identity`, `purchases`, `tonnes`, `certificates`), its own migrations and its own outbox and inbox tables. No module references another; the only shared code is `Carbon.Shared`, and the one cross-module read (a batch's existence and pricing inputs) goes through an interface, `IBatchCatalog`, that Batches implements and Purchases consumes.

```mermaid
flowchart TB
    api["apps/api: composition root, middleware, endpoints"]
    api --> identity & purchases & tonnes & certificates
    identity["libs/identity: users, roles, JWT, refresh tokens"]
    purchases["libs/purchases: schema purchases"]
    tonnes["libs/batches: schema tonnes"]
    certificates["libs/certificates: schema certificates"]
    identity & purchases & tonnes & certificates --> shared["libs/shared: messaging, outbox/inbox, auth primitives, middleware"]
    purchases -. "IBatchCatalog" .-> tonnes
```

Modules still talk only through messages: RabbitMQ carries the commands, Kafka keeps the event log, and the outbox and inbox make delivery at-least-once and consumption idempotent.

## Authentication and authorization

| Piece | How it works |
| --- | --- |
| Accounts | ASP.NET Core Identity. Passwords need 10+ characters with upper case, lower case and a digit, and 4 distinct characters. Five wrong passwords (or codes) lock the account for 15 minutes (`429`). A wrong password and an unknown email give the same `401`. |
| Email | Registration mails a confirmation link; with `Auth:RequireConfirmedEmail` on (the default) sign-in is refused (`403`) until it is opened. "Forgot password" mails a one-time reset link and answers `202` for every address, known or not. Messages go through `IAuthMailer`: SMTP when `Mail:Host` is set, the log otherwise. The demo stack also keeps them in a dev mailbox at `/api/dev/mailbox` (off by default). Link tokens are protected with data-protection keys stored in Postgres, so every instance and every restart can read them. |
| Two-factor | TOTP (any authenticator app): `POST /api/auth/mfa/setup` returns the key and an `otpauth://` URI, `enable` proves it with a code and returns eight one-time recovery codes, `disable` needs the password and a code. Once on, sign-in needs a code (`401 A verification code is required.`). |
| Access token | JWT, 15 minutes, held **in memory** by the Angular app and sent as `Authorization: Bearer`. HS256 with a key of at least 32 bytes, or **RS256** when `Jwt:RsaPrivateKeyPem` is set, in which case the public key is served at `/.well-known/jwks.json` so other services can verify tokens without a secret. The API refuses to start without an usable key. |
| Revocation | Each token carries the user's session version. A password change, a reset, turning two-factor off and **"sign out everywhere"** bump it (one atomic `UPDATE`), and every token allocated earlier stops working on every instance within `Jwt:RevocationCacheSeconds` (default 5). |
| Refresh token | 256 random bits in an `HttpOnly`, `SameSite=Strict`, `Path=/api/auth` cookie (`Secure` over HTTPS). Only its SHA-256 is stored. Every refresh **rotates** it with a conditional `UPDATE`, so two concurrent refreshes with the same token cannot both win. Presenting a token that was already used revokes the whole family. Refresh and logout also need an `X-Requested-With: carbon-web` header, so another site cannot trigger them. |
| Roles | `Customer` (default at registration), `Staff`, `Admin`. An administrator is seeded from configuration on first start. |
| Rules | Anonymous: `/api/health`, `/api/auth/*`. Any signed-in user: read batches, book, read and reverse **their own** purchases and certificates. `Staff` and `Admin`: open batches, read everything, read the event log. Another user's purchase or certificate is `404`, not `403`, so ids do not leak. The owner in a purchase is the caller; a body field cannot set it. |
| Messaging | The outbox is drained by any number of relays at once (`FOR UPDATE SKIP LOCKED`, so no row is published twice at the same time) and trimmed on a schedule. Staff can look inside the dead-letter queues and replay them from the API. |
| Hardening | A rate limit on `/api/auth/*` per client (`RateLimit:AuthPerMinute`), **counted in Postgres** so every API instance shares one count, security headers on every response (`nosniff`, `DENY`, strict CSP, `no-referrer`), a correlation id echoed in `X-Correlation-Id` and in every problem response, JSON logs (`Logging:Json`), forwarded headers only when `ForwardedHeaders:Enabled`, a fallback policy that makes every endpoint authenticated unless it opts out, and a readiness endpoint that checks every module schema. |

## Run it

Needs Node 22 (npm 10), .NET SDK 10.0.401 (`global.json`), Docker (arm64 is fine) and python3 for the smoke script.

```bash
npm ci
dotnet test tests/Carbon.UnitTests   # 125 tests, no Docker
docker compose up -d --wait db        # the integration tests use Postgres on 5478 (database carbon_test)
docker compose exec db createdb -U carbon carbon_test   # once
dotnet test tests/Carbon.IntegrationTests
docker compose up --build --wait      # db, kafka, rabbitmq, api, web
./scripts/smoke.sh                    # last line: carbon smoke passed
npx playwright install chromium       # once
npx playwright test                   # the browser specs against http://127.0.0.1:8098
docker compose down                   # keeps the Postgres volume; `down -v` drops it
```

| Service | Host port |
| --- | --- |
| Web (nginx, proxies `/api/`) | 8098 |
| API (`/scalar/v1`, `/api/health`) | 5218 |
| Postgres 17 | 5478 |
| Kafka 3.8.1 (KRaft, one broker) | 9128 |
| RabbitMQ 3.13 / management UI (`platform` / `platform`) | 5708 / 15708 |

Inside the Compose network the API and nginx listen on 8080 and Kafka's internal listener is 9092.

## Try it with curl

The web app is at <http://127.0.0.1:8098> (`admin@carbon.local` / `CarbonAdmin2026` in the demo stack, or register a customer). From the shell:

```bash
API=http://127.0.0.1:5218
login() { curl -s -X POST $API/api/auth/login -H 'content-type: application/json' -d "{\"email\":\"$1\",\"password\":\"$2\"}" | python3 -c 'import sys,json; print(json.load(sys.stdin)["accessToken"])'; }
ADMIN="Authorization: Bearer $(login admin@carbon.local CarbonAdmin2026)"

curl -s -X POST $API/api/auth/register -H 'content-type: application/json' \
  -d '{"email":"ana@example.com","password":"Passw0rd-Demo1"}'      # 201; its accessToken goes in $ANA below
ANA="Authorization: Bearer <accessToken>"

curl -s -H "$ADMIN" -X POST $API/api/batches \
  -H 'content-type: application/json' \
  -d '{"code":"MANGROVE-2025","name":"Mangrove Delta","standard":"VCS","country":"Ecuador","tonnesIssued":100,"priceCents":1500}'

curl -s -H "$ANA" -X POST $API/api/purchases \
  -H 'content-type: application/json' \
  -d '{"batchCode":"MANGROVE-2025","reference":"ORG-1001","tonnes":10}'
# 202, status Requested, totalCents 15200. Use the returned id:

curl -s -H "$ANA" $API/api/purchases/<id>             # Allocated after a few seconds, with a certificate number
curl -s -H "$ANA" $API/api/certificates/<id>
curl -s -H "$ANA" $API/api/batches/MANGROVE-2025      # tonnesLeft 90
curl -s -H "$ADMIN" $API/api/purchases/<id>/events      # the Kafka log for this purchase

curl -s -H "$ANA" -X POST $API/api/purchases/<id>/reversal \
  -H 'content-type: application/json'                        # tonnes go back to the batch; the certificate stays
```

A purchase is validated at the door: beneficiary reference required (at most 40 characters), 1 to 500 tonnes. The poison match is a case-sensitive substring: `ORG-POISON-7` and any longer token containing `POISON` match.

## Develop with Nx

The repository is an Nx workspace (`nx.json`, plugin `@nx/dotnet`). `npx nx show projects` lists `api`, `web`, the four libraries and both test projects.

```bash
docker compose up -d --wait db kafka rabbitmq   # the brokers and Postgres, not the app
npx nx serve api                                # http://localhost:5218 (Development: seeded admin, public docs)
npx nx serve web                                # http://localhost:4290, proxies /api to the API
WEB_URL=http://localhost:4298 npx playwright test   # the same browser specs, against the dev servers
npx nx run-many -t typecheck lint -p web        # tsc --noEmit and ESLint (angular-eslint)
npx nx build web && npx nx build api            # what CI builds
```

## What is tested, and where it runs

- **Unit tests** (`tests/Carbon.UnitTests`, 125 tests, no Docker, about 3 seconds): the purchase rules and fee, the poison rule including integer overflow, token creation and validation, password and lockout behaviour of the auth service on an in-memory store, owner-or-staff access, the retry policy and topology, message serialization, the outbox `DbContext` conventions, and the HTTP middleware (problem details are `application/problem+json`, security headers, correlation ids).
- **Integration tests** (`tests/Carbon.IntegrationTests`, 67 tests, real Postgres): the tonnes race (two tasks, two `DbContext`s, one unit: one `Held`, one `Short`, no hold for the loser) and the check constraint behind it; the saga handlers against the real schema (duplicate delivery claimed once, a release returns the tonnes once, the poison rejection committed before the dead-letter, reversal then late final mark); and the **whole HTTP pipeline** through `WebApplicationFactory`: register, login, wrong password, lockout, expired and tampered tokens, refresh rotation, replay revoking the family, two concurrent refreshes (exactly one wins), logout, the same-standard header, the auth rate limit, and 401/403/404 for every route and ownership case. The race test was run against a read-then-write version of the hold, where it failed, and against the conditional update, where it passes.
- **`scripts/smoke.sh`** drives the composed stack: authentication and ownership first (401, 403, 404), then validation, the happy path (fee, certificate, Kafka log), the last tonne with three concurrent purchases, the poison reference (rejected, `certificates.work.dlq` gains one message, tonnes restored), and a reversal that lands between the certificate and the final mark. To make that last window wide enough to hit on purpose, Compose sets `Purchases__FinalMarkDelayMs=1500`; it is a latency knob and defaults to 0.
- **Playwright** (`e2e/specs`, 14 tests, Chromium): `auth.spec.ts` (anonymous sees only the form, wrong password, password policy, a customer books and sees only their own purchases with no staff controls, sign-out, the session surviving a reload through the cookie while `document.cookie` stays empty); `buy-credits.spec.ts` (open a batch, book, certificate, reload, reverse); `ui-flows.spec.ts` (refusal messages, the poison rejection, the loser in a race, the Kafka event log). They run against the composed web port and against the Nx dev servers.
- **CI** (`.github/workflows/ci.yml`) has two jobs. `verify`: `npm ci`, Prettier check, `dotnet format whitespace --verify-no-changes`, unit tests, integration tests (Postgres 17 service) with coverage uploaded, a NuGet vulnerability check, `npm audit`, `nx typecheck` and `nx lint` for web (zero warnings allowed) and `nx build`. `e2e`: builds the Compose stack, runs the smoke and every Playwright spec, and uploads traces and prints logs on failure. Dependabot watches NuGet, npm, Actions and Docker.

## Cloud mapping

This repository runs on Compose. Nothing in it calls a cloud account. There is no Pub/Sub client in the process.

| Here | On Google Cloud |
| --- | --- |
| Postgres 17 | Cloud SQL for PostgreSQL |
| Kafka 3.8 | A Pub/Sub topic or managed Kafka |
| RabbitMQ 3.13 | A Pub/Sub subscription with a dead-letter topic |
| The API and the web app | Cloud Run |

## API

JSON over HTTP. Everything except health and `/api/auth/register|login|refresh|logout` needs a bearer token. Interactive docs are at `http://127.0.0.1:5218/scalar/v1` (OpenAPI at `/openapi/v1.json`) when `Docs:Public` is true, which Compose and Development set; otherwise they need a staff token.

| Method and path | What it does | Responses |
| --- | --- | --- |
| `GET /api/health`, `GET /api/health/ready` | Liveness; readiness (every module schema reachable). | `200`, `503` |
| `POST /api/auth/register` | Creates a customer and mails a confirmation link. Signs them in unless confirmation is required. | `201` signed in, `202` check your inbox, `400` policy, `409` email taken, `429` |
| `POST /api/auth/login` | Signs in (with `code` when two-factor is on); sets the refresh cookie. | `200`, `401`, `403` unconfirmed, `429` locked or rate limited |
| `POST /api/auth/refresh`, `POST /api/auth/logout` | Rotate or revoke the refresh cookie. Need `X-Requested-With: carbon-web`. | `200`/`204`, `400` header missing, `401` |
| `POST /api/auth/confirm-email`, `resend-confirmation` | Confirm an address with the emailed token; ask for a new link (always `202`). | `204`/`202`, `400` |
| `POST /api/auth/forgot-password`, `reset-password` | Mail a reset link (always `202`); set a new password with it (ends every session). | `202`/`204`, `400` |
| `POST /api/auth/change-password`, `logout-everywhere` | Signed in: change the password (ends the other sessions and returns a fresh one); end every session on every device. | `200`/`204`, `400`, `401` |
| `GET /api/auth/mfa`, `POST /api/auth/mfa/setup`, `enable`, `disable` | Two-factor status and lifecycle (see above). | `200`/`204`, `400`, `409` |
| `GET /.well-known/jwks.json` | The public key set when RS256 is on. | `200`, `404` |
| `GET /api/auth/me` | The caller. | `200`, `401` |
| `POST /api/batches` | Opens a batch with its tonnes issued and a price in cents per tonne. Staff. | `201`, `400` invalid, `403`, `409` code exists |
| `GET /api/batches`, `GET /api/batches/{code}` | Batches with the live tonnes left. | `200`, `404` |
| `POST /api/purchases` | Accepts a purchase (1 to 500 tonnes) for the caller and starts the saga. | `202` Requested, `400` invalid, `404` unknown batch, `409` duplicate reference |
| `GET /api/purchases?batchCode=&page=&pageSize=` | The caller's purchases, newest first, a page at a time (`pageSize` at most 100, default 100). Staff: everyone's. The total is in `X-Total-Count`. | `200` |
| `GET /api/purchases/{id}` | One purchase and its status. Owner or staff. | `200`, `404` |
| `POST /api/purchases/{id}/reversal` | Moves a held or allocated purchase to `Reversed` and gives the tonnes back. The certificate is not touched. Owner or staff. | `202`, `404`, `409` wrong status |
| `GET /api/purchases/{id}/events` | The Kafka event log for one purchase. Staff. | `200`, `403` |
| `GET /api/admin/dead-letters`, `POST /api/admin/dead-letters/{queue}/replay` | What waits in each dead-letter queue, with the first messages; publish them again. Staff. A message that still fails for the same reason is dead-lettered again. | `200`, `403`, `503` brokers off |
| `GET /api/certificates/{purchaseId}` | The certificate row. Owner or staff. | `200`, `404` |

Errors come back as `application/problem+json` with a `title` and the `correlationId`.

## Configuration

Compose sets these on the API container. Defaults in `apps/api/appsettings.json` point at the published host ports, so `nx serve api` works against `docker compose up -d --wait db kafka rabbitmq`.

| Setting | Compose value | Meaning |
| --- | --- | --- |
| `ConnectionStrings__Carbon` | `Host=db;Port=5432;Database=carbon;Username=carbon;Password=carbon` | The one Postgres database. Migrations run on startup. |
| `RabbitMq__Host` / `__Port` / `__User` / `__Password` | `rabbitmq` / `5672` / `platform` / `platform` | Work queue. Virtual host `/`. |
| `Kafka__BootstrapServers` | `kafka:9092` | Event log. |
| `Jwt__Key` | `${JWT_KEY}` or a demo key | HS256 signing key, at least 32 bytes. Required; startup fails without it. |
| `Jwt__AccessMinutes` / `__RefreshDays` / `__SecureCookies` | `15` / `14` / `false` | Token lifetimes; set `SecureCookies` when TLS ends in front of the API. |
| `Identity__SeedAdmin__Email` / `__Password` | `${ADMIN_EMAIL}` / `${ADMIN_PASSWORD}` | Administrator created on first start. |
| `Auth__RequireConfirmedEmail` / `Auth__PublicUrl` | `false` (demo) / `http://127.0.0.1:8098` | Sign-in waits for email confirmation (code default `true`); the address links in emails point to. |
| `Mail__Host` / `__Port` / `__User` / `__Password` / `__From` / `__StartTls` | unset | SMTP for confirmation and reset emails. Unset means the log only. |
| `Mail__DevMailbox` | `true` (demo) | Serve the last messages at `/api/dev/mailbox`. Never on in production. |
| `Jwt__RsaPrivateKeyPem` / `Jwt__KeyId` | unset | Sign with RS256 and publish the public key at `/.well-known/jwks.json`. |
| `Jwt__RevocationCacheSeconds` | `2` (code default `5`) | How long an instance trusts a cached session version before re-reading it. |
| `RateLimit__AuthPerMinute` | `120` (code default `20`) | Requests a minute per client to `/api/auth/*`. |
| `ForwardedHeaders__Enabled` | `true` | Trust `X-Forwarded-*` from the reverse proxy (nginx here). Leave off when the API is reached directly. |
| `Docs__Public` | `true` | Serve Scalar and OpenAPI without a token. |
| `Logging__Json` | `true` | Structured JSON console logs. |
| `Messaging__OutboxRetentionDays` / `Messaging__InboxRetentionDays` | `7` / `30` | Sent outbox rows and old inbox claims are deleted after this long. The inbox is kept longer because it must outlive any redelivery. |
| `Purchases__FinalMarkDelayMs` | `1500` | Milliseconds the final mark waits. A latency knob so the smoke can land a reversal inside the window. `0` outside Compose. |
| `CARBON_TEST_DB` (tests only) | | Postgres connection for `dotnet test`. Defaults to `localhost:5478`, database `carbon_test`. CI points it at its service container. |

## Look inside and fix things

```bash
docker compose ps                      # five services, all healthy
docker compose logs -f api             # relay, consumers, poison and retry lines
docker compose exec db psql -U carbon -d carbon -c 'select "RoutingKey","SentAt" from "OutboxMessages" order by "Id" desc limit 10'
open http://127.0.0.1:15708        # RabbitMQ management, platform / platform
```

- **Dead-lettered messages** sit in `certificates.work.dlq`. Staff can list and replay them from `/api/admin/dead-letters`, or inspect them in the management UI. Retries wait in `<queue>.retry` for 2 seconds.
- **A port is already in use**: `lsof -nP -i :5218` shows who holds it. Compose publishes only on `127.0.0.1`.
- **`dotnet test` cannot connect**: start Postgres first with `docker compose up -d --wait db`.
- **The smoke cannot reach the API**: run `docker compose up --build --wait` first; override the target with `API=... RABBIT=... ./scripts/smoke.sh`.
- **Start clean**: `docker compose down -v` drops Postgres, Kafka and RabbitMQ data. Plain `down` keeps them, and the smoke is safe on kept data because every code carries a run id.
- **The Kafka log looks empty right after a purchase**: the relay publishes within a second or so; reload the event log.

## Known limits

- Sign-in is email and password plus optional TOTP. There are no external identity providers (Google, Microsoft) and no WebAuthn passkeys.
- Revocation is eventual by design: an instance may honour a revoked access token for up to `Jwt:RevocationCacheSeconds` seconds. Set it to `0` to check the database on every request.
- Delivery is at-least-once, not exactly-once. The inbox makes repeats harmless for the saga; the Kafka log can hold a duplicate event after a relay crash.
- Compose runs one of each broker (one Kafka partition, replication factor 1) and one API instance. The code itself is built for several API instances: the relay claims outbox rows with `FOR UPDATE SKIP LOCKED`, and the rate limit and revocation live in Postgres. A production Kafka and RabbitMQ would be clustered; that is configuration, not code.
- `GET .../events` reads the Kafka topic for one key and gives up after ten seconds, so it scans the topic. Fine here, not something to expose at scale.
- Replaying a dead letter only helps when the cause was fixed: the poison reference fails again by design.
- The browser specs drive Chromium at a desktop size only, and the `e2e` CI job needs Docker and takes a few minutes.

## Layout

```
apps/api       thin host: composition root, middleware, endpoints
apps/web       Angular 21, styled with Tailwind 4 utilities only (sign-in, role-aware workspace)
libs/shared    messaging (outbox, inbox, relay, consumers), auth primitives, middleware
libs/identity  users, roles, JWT, refresh tokens          (schema identity)
libs/purchases  purchase rules and the saga's first step    (schema purchases)
libs/batches  the only writer of the tonnes left     (schema tonnes)
libs/certificates certificates                            (schema certificates)
tests/         Carbon.UnitTests, Carbon.IntegrationTests
e2e/           Playwright
scripts/       smoke.sh
compose.yaml   Postgres, Kafka, RabbitMQ, API, web
```

Nx 23.2.1 orchestrates `nx build web` and `nx build api`; Each module's EF Core migrations are committed under its library and the API applies them on startup. Packages are pinned centrally in `Directory.Packages.props`, and warnings are errors (`Directory.Build.props`).

## Other systems in this .NET track

- [dotnet-freight](https://github.com/joshbarros/dotnet-freight): container space on ocean sailings.
- [dotnet-clinic](https://github.com/joshbarros/dotnet-clinic): appointment slots in a provider session.
- [dotnet-ledger](https://github.com/joshbarros/dotnet-ledger): outgoing payments from an account balance.
- [dotnet-warehouse](https://github.com/joshbarros/dotnet-warehouse): warehouse stock and outbound orders.
- [dotnet-academy](https://github.com/joshbarros/dotnet-academy): course cohort seats and enrollments.
- [dotnet-compute](https://github.com/joshbarros/dotnet-compute): GPU-hour reservations in a pool.
- [dotnet-hotel](https://github.com/joshbarros/dotnet-hotel): hotel room-nights.
- [dotnet-tickets](https://github.com/joshbarros/dotnet-tickets): event ticket tiers and orders.
- [dotnet-charging](https://github.com/joshbarros/dotnet-charging): EV charger slot reservations.
- [dotnet-promo](https://github.com/joshbarros/dotnet-promo): limited-use promo codes and redemptions.
- [dotnet-mint](https://github.com/joshbarros/dotnet-mint): limited-edition drops with a per-collector limit.
- [dotnet-pledge](https://github.com/joshbarros/dotnet-pledge): matched donations from a sponsor's matching pool.
- [dotnet-bistro](https://github.com/joshbarros/dotnet-bistro): table reservations with a first-come waitlist.
- [dotnet-flash](https://github.com/joshbarros/dotnet-flash): flash-sale deals with holds that expire.
- [dotnet-encore](https://github.com/joshbarros/dotnet-encore): gig seats with price steps as the gig sells.
