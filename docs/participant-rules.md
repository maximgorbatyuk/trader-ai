# Participant Rules

Participants are the actors that can hold cash, reserve cash for buy orders, own shares, receive dividends, and appear in the market history. The exact behavior depends on the participant's role.

## Role Pages

- [Individual](roles/individual.md) - default automated trader.
- [AI Agent](roles/ai-agent.md) - trader driven by a hosted language-model provider chosen by the operator.
- [Player](roles/player.md) - human-controlled trader.
- [Company](roles/company.md) - share issuer and listed asset.
- [Collective Fund](roles/collective-fund.md) - pooled automated trader.
- [Fund Member](roles/fund-member.md) - an Individual, or a retained AI Agent membership, while it belongs to a fund.

## Shared Rules

- A buy order reserves cash at the order's limit price and quantity.
- A participant can reserve beyond available cash only within its margin buying power. A fill beyond available cash increases the participant's margin debit without creating an explicit term loan; see [Margin accounts](logic/margin.md).
- Any participant may place a buy or a sell at any price inside the allowed order range around the LULD reference; a price beyond that range is rejected. Continuous matching only crosses orders inside the narrower executable band, so an order in the allowed range but outside the band rests and waits for the band to reach it. See [LULD price controls](rules/luld.md).
- A sell order can list only shares the seller owns and has not already listed in another open sell order.
- Short selling is planned for later and is not implemented: holdings and existing sell commitments always cap sell quantity.
- A participant cannot place a buy while it has an open sell for the same company, or a sell while it has an open buy. A defensive matching rule cancels a newer legacy self-cross without producing a fill, fee, holding change, or price point.
- Closed companies reject both buy and sell orders.
- A crossing buy and sell match at the midpoint of their limit prices.
- A fill changes economic cash and ownership immediately and settles cash and share delivery on the next trading day. Same-day resale is allowed; settled and pending amounts remain visible separately. See [Trade settlement](logic/settlement.md).
- Held shares can receive dividends when the issuing company pays.
- Rule-based automated traders and funds can have stale orders re-priced or cancelled by the market. A configured AI Agent's accepted limit remains exact while its order rests; the order can still fill, be cancelled by the agent, expire at the automated age cap, or be cancelled when a structural market rule invalidates it.
- The Player's orders are managed by the human player, except that stock splits cancel participant orders for the affected company. The player is still subject to the universal allowed-range check: a player order left beyond the allowed range after the band moves is cancelled and its reservation released, even though player orders are never age-managed.
- Sharp price shocks clear ordinary orders priced against the old level: drops clear buy orders, rises clear sell orders.
- LULD price controls preserve resting orders through Limit State and Trading Pause. Orders remain cancellable and may participate in the deterministic reopening auction; see [LULD price controls](rules/luld.md).
- Stock splits preserve holder value by increasing share counts and lowering per-share price.

## Automated Buy Policy

The following rules apply only to rule-based Individuals and configured AI Agents. They do not change the Player's manual orders or Collective Fund decisions.

- The target share exposure is a soft band based on net worth: 20–35% for Low risk, 35–55% for Medium risk, and 50–70% for High risk. A trader above the upper bound creates no new discretionary buys; falling below the lower bound increases buy pull but does not guarantee an order every cycle.
- Open buy reservations reduce exposure headroom, so a trader cannot bypass the band by accumulating unfilled bids. A new order is also limited by default to 1%, 2%, or 3% of net worth for Low, Medium, or High risk respectively, and to 2% of the company's issued shares.
- When below target, a meaningful order must use at least 25% of the currently permitted maximum. Passive interest is capped at 0.25% of the company's issued shares so an unrealistic resting bid cannot dominate the book.
- An Individual crosses the best residual in-band ask at that exact ask price. Demand already allocated earlier in the same decision batch retains price-time priority, preventing a later generated bid from jumping ahead of it.
- When a company has no remaining open sell interest, an eligible rule-based Individual uses a configurable chance to create a bounded passive bid above the current market price. Conservative, Balanced, and Aggressive temperaments use the lower, middle, and upper thirds of the configured premium range respectively; an existing non-executable sell does not qualify as absent supply.
- An AI Agent's envelope can guide a passive bid at the highest price that preserves earlier demand priority. If the agent is below its target exposure and the only residual ask is above that ceiling, no buy envelope is offered because a meaningful below-target buy must cross without jumping earlier demand.
- Low- and Medium-risk automated buys use cash only. High-risk automated buys may use margin, but total margin liability is capped at 10% of net worth. The underlying Player and Collective Fund margin rules are unchanged; see [Margin accounts](logic/margin.md).

AI Agents choose the exact price and quantity themselves inside a current-state envelope and are rejected rather than silently adjusted when they exceed it. An accepted order keeps that exact limit while resting. See [AI Agent](roles/ai-agent.md).
