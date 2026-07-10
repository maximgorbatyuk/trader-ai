# Backend guidance

The root `AGENTS.md` also applies here.

## Domain invariants

- A participant position is one `Holding` per participant and company. Sold-out rows remain with `Quantity == 0`; active reads filter to positive quantities, while insert eligibility checks row existence to avoid the unique-key collision.
- Issuer float is implicit: `IssuedSharesCount` minus all participant holding quantities. Do not create a holding to represent unsold issuer supply.
- Use `PriceSnapshotQueries.LatestPriceByCompanyAsync` for latest-price maps. Do not load and group the full price history inside a cycle service.
- A closed company is soft-deleted through `ClosedInCycleId` and stays available to historical detail, news, and rating lookups. Live-company operations use explicit `ClosedInCycleId == null` filters; do not introduce a global query filter.
- An `Auditor` is a standalone entity, never a `Participant`, because it must not inherit trading, balance, holding, loan, bankruptcy, exit, or fund behavior.
- Participant net worth is cash plus current holding value minus open-loan liability. Keep API responses, decisions, snapshots, and lifecycle checks consistent with that definition.

## Persistence and history

- Price, cash, worth, and sentiment snapshots are forward-only. Do not synthesize history for cycles that predate a snapshot field or feature.
- Live history tables serve runtime queries; aged rows move to archive twins while preserving their identifiers. Reset logic clears live and archive state in dependency order.
- Corporate actions that change price or share denomination must preserve the issued-supply and holding identities documented in `docs/architecture.md`.

## Verification

- After backend code changes, run `rtk dotnet test TraderAi/TraderAi.Tests/TraderAi.Tests.csproj` from the repository root.
