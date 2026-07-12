# Fund advertising

Fund advertising lets the player-managed fund pay to raise its visibility to would-be joiners through a
Popularity Index, which biases how automated traders pick a fund to join.

## Advertising cost

- Only the player-managed fund can buy an advertisement, and it is paid from the fund's own cash.
- The price is a fraction of the fund's worth, where fund worth is its cash plus holdings value minus its
  open-loan liability.
- The fraction slides with the fund's net-worth growth over the last twenty cycles: a fund that is flat or down
  pays the dear fraction of ten percent, and a fund up by twenty percent or more pays the cheap fraction of
  one tenth of a percent, moving linearly and clamped between.
- Growth is measured from the fund's worth snapshots. A fund with fewer than twenty cycles of history measures
  against its earliest recorded snapshot instead.
- An advertisement is refused when the fund cannot cover the price from its spendable cash.
- The player can request a quote showing the current price, the fraction, the growth that set it, the fund
  worth, and the current popularity before committing.

## The Popularity Index

- The Popularity Index starts at zero and has no upper cap.
- Each paid advertisement lifts it by one and stamps the cycle the fund last advertised.
- Popularity ebbs by one each cycle once the fund's last advertisement is more than twenty cycles behind the
  current cycle, or if it has never advertised, and is floored at zero.
- There is no per-cycle cap on how often the fund can advertise.

## Effects

- Buying an advertisement debits the fund's cash as a fund-advertisement cash movement.
- Each advertisement posts a recruiting newswire that carries no market impact.
- An advertisement changes join odds only through the Popularity Index; it moves no price directly.
- When a trader picks a fund to join, popularity is one of the scored terms alongside fund size, worth, recent
  dividends, and recent growth, each min-max normalised across the candidate funds and summed.

The advertising price and payment live in `MarketService`; the popularity decay and its role in fund-join
selection live in `CollectiveFundService`.
