# Fund Member

A Fund Member is normally a rule-based Individual while it belongs to a Collective Fund. Existing AI Agent memberships remain valid, but configured AI Agents no longer enter or switch funds automatically. Membership changes how the trader participates without changing its underlying role.

## Rules

- Only an active, non-bankrupt Individual enters or opens a fund automatically. An AI Agent already in a fund remains a valid member until it leaves.
- A trader cannot belong to more than one fund at a time.
- A trader can join only after the fund-opening window, unless it is completing a fund switch.
- Low-balance traders are eligible to join or open funds, and a long inability to buy raises those chances.
- Joining cancels the trader's open buy orders first.
- The trader contributes most of its settled, unreserved cash as a fund deposit; unsettled sale proceeds stay outside the fund until settlement.
- While membership lasts, the trader stops bidding for itself. The fund handles buying with pooled capital.
- A member may still sell shares it already personally owns.
- A member cannot sell shares it does not own; short selling is planned for later and is not implemented.
- Its personal trades still use T+1 settlement, and any existing personal margin debit remains separate from the fund's account and explicit term loans.
- A member receives its share of the fund's dividend pass-through, divided by deposit size and net of some fund management fee.
- A member also receives ordinary dividends on any shares it still personally holds.
- A member cannot voluntarily leave during its first seven trading days in the fund. A member that joins on Day N first becomes eligible on Day N+7; intraday cycles, breaks, and market pauses do not shorten that period.
- After the safe period, a member may leave at any personal cash balance. Its chance to leave rises with each eligible cycle until it reaches the configured cap.
- On ordinary leave, the fund returns the member's full deposit the same cycle. If the fund is short on cash, it borrows the shortfall to pay in full rather than making the member wait for share sales.
- A non-founder Individual can switch to a stronger fund after the seven-trading-day safe period. Aggressive members switch more readily, conservative members less readily; AI Agents do not switch automatically.
- A fund snapshots its daily voluntary-departure quota from its member count at the start of the trading day: 15%, rounded up so a non-empty fund can release at least one member. Ordinary leaves and switches consume the quota; administrative removals such as fund closure or capacity enforcement do not.
- A founder does not switch away from the fund it opened.
- A switching member joins the best available fund once its old membership is settled, even if its returned cash would normally put it over the join limit.
- If a closing fund returns only a small fraction of a member's deposit, that member gets one later chance to leave the market after it is shareless and out of any fund.
- Fund closure, capacity enforcement, and removal of a member that already left the market can end a membership during the safe period because they are administrative removals rather than voluntary departures.
