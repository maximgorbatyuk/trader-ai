# Market service guidance

The root and backend `AGENTS.md` files also apply here.

## Cycle ordering

The pre-match order is intentional:

1. Revise industry sentiment.
2. Maintain ordinary participant orders and persist released cash and shares.
3. Run volatility halts.
4. Apply stock splits or reverse merges.
5. Apply free-share emissions.
6. Replenish scarce issuer float with demand-paced primary issuance.
7. Fund a company through a big investment, minting shares before closure so the deal's delisting protection is honoured this cycle.
8. Update scheduled company financial reports from the settled sentiment and corporate-action state.
9. Process company closure and appearance.
10. Liquidate cash-starved holders.
11. Service loans and create distress sells.
12. Process bankruptcy.
13. Process collective funds.
14. Process participant exits and replacements.
15. Apply the concentration cap.
16. Run auditors last.

Do not reorder these phases without reviewing which prices, ratings, holdings, orders, and crisis state each later service is expected to observe. Preserve existing save boundaries where a later database query must see an earlier phase's staged changes.

Financial snapshots are saved before lifecycle and audit work so those consumers share one current-cycle report.
The initial roster and every new listing use the same financial seed path after the company id and first price exist.
Manual `AdvanceCycleAsync` is a matching/advance helper and does not run pre-match services, including financial reporting.

## Order resting

In the live tick (`RunCycleTickAsync` → `DecideAndAdvanceCoreAsync`), matching passes `holdNewOrders`, so any order created during the current cycle — the decision pass's bids and asks, forced service sells, and newly issued float alike — rests until a later cycle's matching. This makes every order visible in the book to the player and other participants for at least one cycle before it can cross. Price, rating, holding, and cash effects staged by the pre-match services still take effect in the same cycle; only match eligibility of the newly created orders is deferred. The manual `AdvanceCycleAsync` path and direct `MatchingEngine.RunAsync(cycle)` calls default to no hold and keep crossing whatever is already in the book.

## Service shape and randomness

- Per-cycle services are opt-in through their options and stage changes on the shared `AppDbContext` unless the orchestrator explicitly saves between phases.
- Put tunable event chances, chance modifiers, and random magnitude bands in `RandomChanceRatesOptions`; do not introduce a private probability constant in a service.
- Deterministic services must not draw random values. Randomized services preserve their documented draw order because tests use scripted `Random` queues.
- In a dividend window, `MarketService` draws dividend decisions and rates for all priced companies by ascending ID before drawing independent operating-income decisions and rates in the same order.
- Scale a probability threshold without adding a draw on unaffected branches. When a feature requires a new draw, update the service's draw-discipline comment and all affected scripted tests.
- Use the shared price map and batch decision writes. Do not add per-trader latest-price queries or per-order `SaveChangesAsync` calls to the decision pass.

## Trading and actor exceptions

- Ordinary order ageing, repricing, bankruptcy, exit, and automated decisions do not manage the human player.
- Splits, reverse merges, volatility halts, and loan-distress liquidation are deliberate exceptions that may cancel or force action on player positions.
- Automated cash-raising sells (bankruptcy fire-sales, loan-distress liquidation, fund cash raises) floor their ask at the company's active lower price band, because matching skips any order resting outside the band.
- A player-managed collective fund is skipped by automated fund trading and fund-level automatic closure, while ordinary membership processing still applies.
- A fund short on cash to repay a leaving member borrows the shortfall plus the configured buffer and pays in full that cycle instead of force-selling; the payout loan ignores the debt cap and, because loan servicing precedes fund processing, is first serviced the next cycle. Force-selling to fund a leave now runs only as the loans-disabled fallback.
- Secondary participant sales may pay the configured trade fee; issuer-float sales do not. Loan interest is bank revenue, while principal repayment only reduces the liability.
- Scoped news impacts settle at the advance boundary so the next decision cycle reacts to them rather than trading against an unsettled book.

## Verification

- Run the full backend suite after service changes: `rtk dotnet test TraderAi/TraderAi.Tests/TraderAi.Tests.csproj`.
- When random draws or cycle ordering change, run the directly affected tests first, then the full suite.
