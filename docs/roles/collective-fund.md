# Collective Fund

A Collective Fund is a pooled automated trader created during the simulation. It trades with members' contributed cash and owns shares as its own market participant.

## Rules

- A fund is opened by an eligible Individual or AI Agent after the fund-opening window.
- The founder becomes the first member.
- The fund keeps the founder's temperament and risk profile from the moment it is created. Later changes to the founder do not change the fund's trading personality.
- Members contribute most of their settled, unreserved cash as deposits. The fund trades that pooled capital.
- An active fund buys and sells automatically like an automated trader, but normally keeps 10% of its worth as a cash buffer for member deposits. From the trading day before any member becomes leave-eligible, and while a departure remains possible, its automated buying uses a 15% buffer instead. Its ordinary discretionary orders follow the same band-aware pricing as an Individual, while its cash-raising and liquidation sells are clamped into the executable band so they can cross. See [LULD price controls](../rules/luld.md).
- A fund keeps the same margin-account model as other market participants, but automated fund buys use only settled cash above the payout buffer. A player-managed fund can still use available margin buying power through manual orders, and margin debit remains separate from any explicit term loan used for a member payout.
- A fund can hold shares, receive dividends, and sell holdings.
- On the day before a member becomes leave-eligible, an AI-managed fund cancels its open buys and lists only enough holdings to move toward the 15% settled-cash target. Those sales settle when the next trading day opens; a player-managed fund remains under manual control and does not receive these automatic orders or cancellations.
- Fund trades change its economic position immediately and settle on T+1. The managed-fund view separates settled and pending cash and shares.
- Short selling is planned for later and is not implemented.
- A fund passes part of its own dividend income through to members, divided by deposit size, and withholds a 5% management fee from each member's share that stays in the fund's cash.
- A fund has a configurable member capacity, bounded by its configured maximum. Once it reaches capacity it stops taking new members, and a fund found above capacity returns its most recently joined member's deposit and drops that member — one per cycle, by the standard leave rules — until it is back within capacity. Capacity enforcement can remove a member during the seven-day safe period and applies to the player-managed fund as well.
- When several funds have room, joiners lean toward stronger funds — higher net worth, better recent dividend income, faster recent growth, and heavier advertised popularity — and toward funds with more room to spare. A joiner is drawn to a fund with a chance proportional to that strength rather than always to the single strongest, so members spread across good funds and a fund near its member cap attracts fewer newcomers.
- A fund that is winding down stops buying. It cancels open buys, lists remaining holdings for sale, and closes once it no longer owns shares, has no pending settlement, and has no margin liability.
- When a fund is short on cash to return a leaving member's deposit, it borrows the shortfall plus a small buffer and pays the member in full the same cycle, then carries that loan. Only when lending is disabled does it fall back to selling shares at a discount and making the member wait.
- A fund releases at most one voluntarily leaving member per trading day. Once the day's leaver has been repaid, any other member wanting out waits for the next trading day, even when several are ready to leave at once. The limit covers both an ordinary leave and a switch to another fund, but not administrative removals — capacity enforcement, fund closure, and a member leaving the market.
- Margin interest accrues once per trading day, sale proceeds repay margin liability first, and a maintenance deficiency can create forced-sale orders from settled holdings.
- If only two members remain and one leaves, the whole fund winds down.
- The founder can close a fund after severe loss from its peak value or after recent dividend starvation.
- A fund that owns no shares and cannot afford the cheapest share for a long stretch also winds down.
- When a fund closes, remaining settled cash is split between surviving members and the fund becomes inactive.
- A fund does not go bankrupt.
