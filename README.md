# NCBRS — National Civil Birth Registration System

A birth registration platform for a Ministry of Health, built for hospitals,
clinics and **offline-first village health posts**. A village clinic can be
disconnected for weeks and must still register a birth and print a
certificate before the family leaves the room.

That constraint shapes everything else. Start with
[CLAUDE.md](CLAUDE.md) — it records *why* each decision was made, which
matters more here than what the code does.

## Status

| Tier | State |
|---|---|
| Central API, certificates, dedup, event backbone | Built and running |
| District store-and-forward node | Built |
| Reporting projection and indicator queries | Built |
| Facility / village tablet client | **Not started** — the programme's critical path |
| Central management web front end | **Being planned** |

The sequenced build plan, gap analysis and business case live in
[NCBRS-Business-and-Delivery-Plan.md](NCBRS-Business-and-Delivery-Plan.md).
Its §12 and §15 are the plan of record for what to build next.

## Layout

```
src/NCBRS.Core                — models, DbContext, events, SQLite migrations
src/NCBRS.Api                 — HTTP only; stages events in an outbox
src/NCBRS.Relay               — drains the outbox to Kafka
src/NCBRS.Consumer            — projections, dashboard queries, DHIS2 export
src/NCBRS.District            — Tier 2 store-and-forward node
src/NCBRS.Migrations.Postgres — the central tier's migration history
tests/NCBRS.Tests             — 566 tests
```

The services are separate because they fail and scale differently: a broker
outage stalls delivery without touching registrations, and neither worker can
take the registration API down with it.

## Running it

Requires .NET 10 SDK and Docker.

```bash
cp .env.example .env
```

```bash
docker compose up -d
```

Compose refuses to start without the variables `.env` supplies rather than
falling back to defaults — a default password is a password everyone has.

Then each service in its own terminal:

```bash
dotnet run --project src/NCBRS.Api
```

```bash
dotnet run --project src/NCBRS.Relay
```

```bash
dotnet run --project src/NCBRS.Consumer
```

```bash
dotnet run --project src/NCBRS.District
```

| Service | Port | |
|---|---|---|
| API | 5259 | Swagger at `/swagger` |
| District node | 5280 | |
| Consumer | 5281 | Dashboard and exports |
| Keycloak | 8080 | Realm imported on startup |
| Kafka | 9092 | |
| PostgreSQL | 5433 | Not 5432 — see CLAUDE.md |

The API alone is enough for registration, certificates and amendments: the
request path never touches Kafka, so events simply queue in the outbox until
the relay runs.

## Tests

```bash
dotnet test
```

To run the same suite against PostgreSQL rather than SQLite:

```bash
NCBRS_TEST_PROVIDER=Postgres dotnet test
```

## Development credentials

Infrastructure credentials — PostgreSQL and the Keycloak admin — come from
`.env`, which is not committed. `.env.example` is the template.

The four registrar accounts (`nurse.banda`, `dr.tembo`, `district.officer`,
`ministry.admin`, all with the password `password`) are **committed** in
`keycloak/ncbrs-realm.json`. They are not in `.env` because Keycloak's realm
import does not substitute environment variables — that was tested, and a
placeholder becomes the literal password, which looks secured while being
trivially guessable.

> **This realm is development fixture data and must never be imported into a
> Keycloak that anyone else can reach.** Compose runs it with `start-dev`, on
> an in-memory database over plain HTTP, which is not a production mode in any
> case.

Nothing here is a production credential, and the platform refuses to start
outside Development without a real certificate signing key configured.

## Licence

Not yet chosen — see the repository owner before reuse.
