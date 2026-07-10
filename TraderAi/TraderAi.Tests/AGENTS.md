# Backend test guidance

The root `AGENTS.md` also applies here.

## Test structure

- The backend suite uses xUnit and tests the production project directly. Prefer existing seed and context helpers over building parallel fixtures.
- Put behavior tests beside the closest service or API test class. Add an integration test when correctness depends on service ordering, persistence, or more than one subsystem.
- Assert externally meaningful state such as balances, reservations, holdings, orders, snapshots, and event records rather than private implementation steps.

## Randomized services

- Many service tests use queued or scripted `Random` implementations. Queue order is part of the test contract.
- When production code adds, removes, or moves a random draw, update every affected queue and keep the service's draw-discipline comment accurate.
- Disabled and deterministic branches should consume no unexpected draws. Prefer explicit queue exhaustion when the test is meant to prove draw discipline.
- Preserve seeded-market sequences unless the test deliberately changes seed behavior.

## Verification

- Run a focused test while iterating, then run the complete suite before reporting completion:
  `rtk dotnet test TraderAi/TraderAi.Tests/TraderAi.Tests.csproj`
