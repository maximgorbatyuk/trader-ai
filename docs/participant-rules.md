# Participant Rules

Participants are the actors that can hold cash, reserve cash for buy orders, own shares, receive dividends, and appear in the market history. The exact behavior depends on the participant's role.

## Role Pages

- [Individual](roles/individual.md) - default automated trader.
- [AI Agent](roles/ai-agent.md) - trader driven by a hosted language-model provider chosen by the operator.
- [Player](roles/player.md) - human-controlled trader.
- [Company](roles/company.md) - share issuer and listed asset.
- [Collective Fund](roles/collective-fund.md) - pooled automated trader.
- [Fund Member](roles/fund-member.md) - Individual or AI Agent while it belongs to a fund.

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
- Automated traders and funds can have stale orders re-priced or cancelled by the market. Repricing moves a stale order toward the executable band and clamps it there, so an aged limit never compounds far past the band.
- The Player's orders are managed by the human player, except that stock splits cancel participant orders for the affected company. The player is still subject to the universal allowed-range check: a player order left beyond the allowed range after the band moves is cancelled and its reservation released, even though player orders are never age-managed.
- Sharp price shocks clear ordinary orders priced against the old level: drops clear buy orders, rises clear sell orders.
- LULD price controls preserve resting orders through Limit State and Trading Pause. Orders remain cancellable and may participate in the deterministic reopening auction; see [LULD price controls](rules/luld.md).
- Stock splits preserve holder value by increasing share counts and lowering per-share price.
