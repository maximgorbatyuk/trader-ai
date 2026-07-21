# Trading days

Trader AI uses a compressed, deterministic trading-day schedule. It borrows the open-versus-closed distinction of an equities market without reproducing a real exchange calendar or clock.

## Schedule

| Phase | Duration while running | Trading-cycle effect | Trading behavior |
| --- | ---: | --- | --- |
| Trading | 7 minutes | Advances from cycle 1 through cycle 210 | New orders, matching, and cancellations are available. |
| Break | 1 minute | Holds at cycle 210 | New orders and matching stop; cancellations remain available. |

Each trading cycle represents two seconds of active simulation time. A complete day-to-day loop therefore lasts eight minutes while the market is running: seven minutes of trading followed by one minute of break.

Finishing cycle 210 activates one break cycle for that trading day. The break has its own countdown, but it never creates or increments a trading cycle. When the countdown reaches zero, the break completes and the next numbered day opens at cycle 1.

## Pause and reset

Pausing the market freezes both the trading countdown and the break countdown. Starting it again resumes the active phase from the same remaining value.

Resetting the market returns the clock to day 1 before its first trading cycle.

## Frontend display

The top navigation always shows the numbered day, the active phase, cycle progress, cycles remaining, and time remaining. During trading, an example is:

```text
Day 7 · Trading    Cycle 84/210 · 126 left    04:12 left
```

During the break, the trading-cycle values remain fixed while only the break countdown changes:

```text
Day 7 · Break      Cycle 210/210 · 0 left     00:43 left
```

The countdown updates between server refreshes, freezes visibly when the market is paused, and is corrected by the next server response. Phase and timer information is presented as text so it does not depend on color alone.

## Trading-day boundaries

Trading-day numbers are unique and increase one at a time. A trade executed at any point on Day N is due for T+1 settlement when Day N+1 opens; the one-minute break does not count as another day. Due instructions update settled cash and shares together at the new day boundary. See [Trade settlement](../logic/settlement.md).

Margin debit interest also uses the trading-day boundary and accrues at most once per day. It does not accrue once per cycle or during the break. See [Margin accounts](../logic/margin.md).

Collective-fund membership uses the same boundary. A member that joins on Day N cannot voluntarily leave until Day N+7. On Day N+6 an AI-managed fund starts preparing for that possibility by moving from its ordinary 10% cash buffer toward a 15% buffer; sales placed for that preparation can settle when Day N+7 opens. See [Fund Member](../roles/fund-member.md) and [Collective Fund](../roles/collective-fund.md).

Each day close also records one worth snapshot per participant into a compact daily series. That series backs the multi-day total-worth chart, which plots recent day closes together with a live current-worth point for the open day.

Security-specific LULD durations count active trading cycles. A market-wide break or manual market pause therefore freezes a company's limit-state and trading-pause countdown along with the main clock. Security-specific LULD states do not stop the market-wide day clock while other securities continue to trade. See [LULD price controls](luld.md).

## Where to see it

- The top navigation shows **Day N · Trading** or **Day N · Break**.
- **Cycle X/210 · Y left** shows completed-day progress and cycles remaining. It remains at **Cycle 210/210 · 0 left** throughout the break.
- The adjacent **MM:SS left** timer counts down the active trading phase or the separate break phase.
- During a pause, both displayed countdowns remain frozen until the market resumes.
- Trader and player settlement tables show the trade day and due day calculated from this clock.

Weekends, public holidays, extended-hours sessions, overnight orders, and a literal U.S. exchange timezone are outside this schedule.
