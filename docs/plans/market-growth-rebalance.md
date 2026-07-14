# Market growth rebalance plan

Goal: stop capitalization from drifting down and let it grow, by (1) refilling exhausted float with
demand-paced cash-absorbing issuance, (2) letting real demand lift clearing prices, and (3) removing the
structural downward bias in bands and events. The 863:1 cash overhang is intentionally left in place — the
market is expected to run "hot" (strong upward repricing).

## Diagnosis this addresses

- Float is 97.4% sold (16,337 of 615,839 shares buyable) → 96% of buy orders cancel unfilled.
- Midpoint execution + symmetric offsets → clearing price ≈ last price (no upward driver).
- Asymmetric band (+10%/-15%) and one-directional auditor cuts (-15% to -35%) → net downward drift.

## 1. Demand-paced cash-absorbing issuance (new)

New service, separate from `ShareEmissionService` (which is free dilution gated at $500M cap and never
fires here).

- Trigger: a live company whose unsold issuer float (`IssuedSharesCount` - sum of participant holdings)
  is below `FloatScarcityThresholdPercent` (default 10%) of issued shares.
- Size: mint `IssuanceRateMin`..`IssuanceRateMax` of current issued shares (default 2%-20%).
- Listing: one company-originated sell order (`ParticipantId = null`) resting at the current price, so it
  fills through ordinary matching; proceeds settle to corporate cash via the existing PrimaryIssuance path.
- Cooldown: at most one issuance per company per trading day.
- Self-throttling: after issuance float returns above the threshold and stays quiet until demand
  re-absorbs it below the threshold, so cumulative dilution is paced by real demand, not the clock.

Files: new `PrimaryIssuanceService` (+ options) wired into the pre-match window in `MarketService`,
alongside the other supply-side corporate actions. Reuses the float math already used for the issuer float.

## 2. Demand-responsive pricing

Let persistent unmet buy demand lift the price instead of being discarded on cancel.

- Mechanism (recommended, least invasive): a per-company reference-price ratchet. When a company ends a
  matching cycle with in-band buy demand that exceeded available sell supply, nudge its LULD reference
  price up by a small bounded step so next cycle's orders form higher.
- Alternative: bias execution price toward the buyer's limit in proportion to buy/sell depth imbalance
  (instead of pure midpoint in `MatchingEngine`).
- Tuning: start with a small step; this is the growth engine and the most tuning-sensitive change. Verify
  it produces strong-but-not-explosive repricing given the 863:1 demand.

Files: `VolatilityHaltService` (reference computation) for the ratchet, or `MatchingEngine` for the
execution-bias variant.

## 3. Remove structural downward bias

Config (`appsettings.json` / options defaults):

- `VolatilityHalt.UpperBandPercent`: 10 -> 15 (symmetric with the 15% lower band).
- `VolatilityHalt.AllowedOrderUpperPercent`: 15 -> 25 (symmetric with the 25% lower allowed range).
- `TradeFee.FeeRate`: 0.01 -> 0.005 (halve the seller fee; less erosion on every secondary sale, so more
  proceeds stay with participants and recirculate as buying power).
- Dividends x3: `RandomMagnitudeBands.DividendRateMin` 0.0001 -> 0.0003, `DividendRateMax` 0.005 -> 0.015.
  (Cosmetic for cap; returns corporate cash to participants. Kept per request.)

Auditor is the real down-bias, not the newswire (newswire is already ~50/50). Two parts:

### 3a. New positive verdict: "Raise expectations" (mirrors the negative Extra path)

- New `CompanyRiskRating.RaisedExpectations` enum value.
- Trigger: a separate positive roll (`AuditorRaiseExpectationsChance`, new key in `EventTriggerChances`)
  taken only when no issue was found, so a company that would otherwise be rated Low/High can instead earn
  the upgrade. "Sometimes" = modest default (e.g. 0.08), tunable.
- Effect: immediate lift of a random +5% to +15% (mirrors the cut's range) via
  `MarketImpactService.ApplyImpactAsync(Increase, [company], pct, ...)`, then cancel every open/partial
  participant sell order on the company so owners re-list at the new price. Apply the bump before the cancel
  so next cycle's decision engine re-forms sells above the old level. New `MinRaisePercent`/`MaxRaisePercent`
  constants (5/15) mirror the cut's `Min`/`MaxIssueDropPercent`.
- New `ReviseSellOrdersAsync` mirroring `ReviseBuyOrdersAsync`, but: (a) a blanket cancel per the request
  ("all sell-orders ... cancelled"), not personality-weighted; (b) sells reserve shares not cash, so
  `CancelSell` is just a status change (freed share commitment is implicit); (c) still skip the Player and
  bankrupt owners, preserving the existing "market never auto-cancels the player" invariant.
- Records a `CompanyRating` with the new verdict and a positive `NewsPost` (Direction = Increase,
  Scope = Company, ImpactPercent = 1); new `DemoAuditContent.RaisedExpectations(...)` copy. Not added to the
  crisis timeline (it is good news).
- Draw discipline: gate the new positive roll (and its magnitude draw) so that when
  `AuditorRaiseExpectationsChance` is 0 nothing is drawn, keeping the existing scripted-Random draw order
  (and existing seeded tests) intact. When enabled, the upgrade path draws one NextDouble for the roll and
  one more for the lift size, mirroring the Extra cut.

### 3b. Softened, near-symmetric cut band

Cut band `AuditorService` `MinIssueDropPercent`/`MaxIssueDropPercent` 15/35 -> **10/20**. Paired with the
+5%/+15% upgrade this leaves only a mild residual down-lean per event (cut avg -15% vs lift avg +10%), which
is further offset by upgrade frequency: on stable companies the expected lift (0.08 x 10%) exceeds the
expected cut (0.02 x 15%), so the auditor turns roughly neutral-to-positive on the calm majority and only
net-negative on already-volatile names. Leave the newswire alone.

## Open tuning knobs (defaults chosen, adjust on review)

- Issuance band 2%-20% and scarcity threshold 10%.
- #2 ratchet step size.
- Auditor: `AuditorRaiseExpectationsChance` (default 0.08), upgrade lift band 5/15, cut band 10/20.

## Verification

- `rtk dotnet test TraderAi/TraderAi.Tests/TraderAi.Tests.csproj` (full suite; several existing tests
  assert band/price/issuance behavior and will need updating).
- Run against a separate port + temp DB (not the dev :5100 / live DB) for a multi-day soak, then re-check:
  total market cap trend, fill-rate by side, price up/down move symmetry, and corporate-cash growth.
