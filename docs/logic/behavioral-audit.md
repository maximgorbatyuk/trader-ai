# Behavioural audit

The behavioural audit is a periodic review that scores each active trader on how it has traded recently and uses
those scores to reclassify the Player and the Player's Fund into the personalities the market already recognises.

## Cadence

- The audit runs every thirtieth cycle and looks back over the last thirty cycles of activity.
- Cycles that are not a multiple of thirty leave every score and personality untouched.

## The five metrics

Over the window, each audited trader is measured on five metrics drawn from its own orders and trades:

- average orders placed per cycle;
- total orders placed;
- average order price per share;
- average shares per order;
- average distance each trade executed from the market price just before it, taken as an absolute fraction.

For the last metric, the price a trade moved away from is the price recorded immediately before that trade, and
a trade counts for both its buyer and its seller.

## Indices

- Each metric is min-max normalised across the whole audited population, so every metric contributes on a
  zero-to-one scale.
- The **Temperament Index** is the sum of all five normalised metrics.
- The **Risk Profile Index** is the sum of the three that read as risk appetite: trade frequency, order size, and
  how far trades push the market.
- Both indices are stored on every audited trader.

## Classification

- The traders whose personalities are fixed — everyone except the Player and the Player's Fund — form reference
  clusters: the average Temperament Index for each Temperament, and the average Risk Profile Index for each Risk
  Profile.
- The Player and the Player's Fund are each snapped to the nearest cluster average on each axis: nearest
  Temperament by Temperament Index, nearest Risk Profile by Risk Profile Index. Ties fall to the earliest value.
- The Temperaments are Aggressive, Balanced, and Conservative; the Risk Profiles are High, Medium, and Low, with
  the Balanced temperament aligning to the Medium risk profile.
- Only the Player and the Player's Fund are reassigned; every other trader keeps its personality even when its own
  activity would place it in a different cluster.
- The reclassification is announced as a newswire with no market impact.

The audit logic lives in `BehaviorAuditService`.
