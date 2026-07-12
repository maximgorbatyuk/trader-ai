# Collective Fund

A Collective Fund is a pooled automated trader created during the simulation. It trades with members' contributed cash and owns shares as its own market participant.

## Rules

- A fund is opened by an eligible Individual or AI Agent after the fund-opening window.
- The founder becomes the first member.
- The fund keeps the founder's temperament and risk profile from the moment it is created. Later changes to the founder do not change the fund's trading personality.
- Members contribute most of their cash as deposits. The fund trades that pooled capital.
- An active fund buys and sells automatically like an automated trader, but keeps a cash buffer so it can return member deposits.
- A fund can use the same debt allowance as other market participants, after accounting for its cash buffer.
- A fund can hold shares, receive dividends, and sell holdings.
- A fund passes part of its own dividend income through to members, divided by deposit size.
- A fund can hold up to twenty members.
- When several funds have room, joiners prefer stronger funds: larger membership, higher net worth, better recent dividend income, faster recent growth, and heavier advertised popularity.
- A fund that is winding down stops buying. It cancels open buys, lists remaining holdings for sale, and closes once it no longer owns shares.
- When a fund is short on cash to return a leaving member's deposit, it borrows the shortfall plus a small buffer and pays the member in full the same cycle, then carries that loan. Only when lending is disabled does it fall back to selling shares at a discount and making the member wait.
- If only two members remain and one leaves, the whole fund winds down.
- The founder can close a fund after severe loss from its peak value or after recent dividend starvation.
- A fund that owns no shares and cannot afford the cheapest share for a long stretch also winds down.
- When a fund closes, remaining cash is split between surviving members and the fund becomes inactive.
- A fund does not go bankrupt.
