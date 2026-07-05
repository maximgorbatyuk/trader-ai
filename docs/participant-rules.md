# Participant Rules

Participants are the actors that can hold cash, reserve cash for buy orders, own shares, receive dividends, and appear in the market history. The exact behavior depends on the participant's role.

## Role Pages

- [Individual](roles/individual.md) - default automated trader.
- [AI Agent](roles/ai-agent.md) - automated agent-style trader, currently following Individual rules.
- [Player](roles/player.md) - human-controlled trader.
- [Company](roles/company.md) - share issuer and listed asset.
- [Collective Fund](roles/collective-fund.md) - pooled automated trader.
- [Fund Member](roles/fund-member.md) - Individual or AI Agent while it belongs to a fund.

## Shared Rules

- A buy order reserves cash at the order's limit price and quantity.
- A participant can reserve beyond available cash only within the market's debt allowance. Debt is represented as a negative balance, and later income pays it down first.
- A sell order can list only shares the seller owns and has not already listed in another open sell order.
- A crossing buy and sell match at the midpoint of their limit prices.
- Held shares can receive dividends when the issuing company pays.
- Automated traders and funds can have stale orders re-priced or cancelled by the market.
- The Player's orders are managed by the human player, except that stock splits cancel participant orders for the split company.
- Sharp price shocks clear ordinary orders priced against the old level: drops clear buy orders, rises clear sell orders.
- Stock splits preserve holder value by increasing share counts and lowering per-share price.
