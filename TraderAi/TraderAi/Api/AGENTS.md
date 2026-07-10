# API guidance

The root and backend `AGENTS.md` files also apply here.

## Endpoint contracts

- Keep the server-paged participant and company roster endpoints separate from the plain-array endpoints used by dashboard maps and client-side aggregates.
- Paged endpoints own search, sorting, paging, and their supported filters. Keep table sort keys and response fields synchronized with the frontend callers.
- Detail endpoints must continue to resolve retained closed companies and inactive historical entities where their standalone routes require it.
- Participant-scoped holdings, orders, attention, worth history, cash movement, and loan endpoints serve individuals, the player, and the player's managed fund. Avoid actor-specific copies of the same read contract.
- When a response field changes, search all frontend consumers and API tests before editing the contract.

## Query behavior

- Use the shared latest-price lookup for current valuation.
- Keep live-market roster queries explicit about closed or inactive entities rather than relying on a global database filter.
- Preserve server paging for potentially large rosters and histories; use unpaged arrays only where the caller intentionally needs the complete live set.

## Verification

- Cover endpoint changes in `ApiTests`, `MarketApiTests`, or the closest participant-specific integration tests.
- Run `rtk dotnet test TraderAi/TraderAi.Tests/TraderAi.Tests.csproj` after API changes.
