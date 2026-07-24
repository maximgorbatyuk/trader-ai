# Auditors

An Auditor is an independent rating agency. Auditors convert completed company and market evidence into an immutable status that becomes a forward-looking trading signal on the following trading day. An audit does not directly change price, cancel orders, or delist a company.

## Cadence and effective day

- Every live company is due for one audit after each two-trading-day evidence window.
- Audits are created only in the first cycle of their effective trading day. For example, evidence from Days 1–2 becomes effective on Day 3; the next window covers Days 3–4 and becomes effective on Day 5.
- A newly listed company starts its first window on its listing day. A company is not audited until that window contains at least one completed trading day.
- Auditors are created with the market and are restored automatically if a running market has none. Their count scales at roughly one agency per twenty companies, and due companies are assigned across them deterministically.
- Each stored audit keeps its evaluation start and end days, effective day, rule version, total score, component scores, and the evidence used to produce them.

The decision system reads only the newest audit whose effective day has arrived. This prevents an unfinished current day from leaking into the verdict.

## Evidence and scoring

The audit evaluates the completed window rather than making a random verdict. Its score combines:

- split-adjusted price return;
- the largest single-cycle price move, excluding split and reverse-split boundaries;
- dilution from free-share emissions;
- stock splits and reverse splits;
- the latest actual dividend outcome and expected-dividend coverage;
- the industry's Rising, Plateau, or Falling direction;
- profitability;
- financial volatility;
- closure risk;
- management outlook weighted by management confidence.

The evidence keeps opening and closing prices, issued and emitted shares, denomination-event counts, issuer cash, dividend values, industry sentiment, and the linked [company financial snapshot](../logic/company-fundamentals.md). Primary issuance and direct investment are accounted for when reconstructing the opening supply, so they are not mistaken for free-share dilution.

Scores use configurable thresholds and are clamped to a bounded total. Strong favorable evidence adds to the total; adverse returns, jumps, dilution, reverse splits, reduced or skipped dividends, falling industries, weak profitability, high volatility, high closure risk, and negative guidance reduce it.

## Five statuses

From strongest outlook to highest risk, the only current statuses are:

1. **Extra raised expectations**
2. **Raised expectations**
3. **Stable**
4. **Low risk**
5. **High risk**

Positive and negative score thresholds select the two raised-expectations and two risk statuses. A score in the neutral band becomes **Stable** only when financial evidence exists, expected dividends are covered, reported operating cash flow is non-negative, and closure risk is not High. Otherwise the neutral result is conservatively downgraded to **Low risk**.

The status and its component evidence are shown on the company page. The **Audits** tab contains the paged audit history; opening an entry shows the complete immutable evidence and scoring breakdown.

## Effect on trading

An effective audit is one input to the normalized directional signal used by rule-based traders and funds:

- **Extra raised expectations** contributes a strong positive direction.
- **Raised expectations** contributes a moderate positive direction.
- **Stable** is neutral.
- **Low risk** contributes a moderate negative direction.
- **High risk** contributes a strong negative direction.

The audit component is combined with current fundamentals, price momentum, order-flow imbalance, and industry sentiment. It changes buy, sell, and wait probabilities but never makes an outcome certain. The same effective audit and evidence are included in AI Agent snapshots. See [Share price formation](../rules/share-price-formation.md) and [Participant rules](../participant-rules.md).

## Portfolio audit news

When a new audit batch includes a company held by the player or the player's managed fund, the Newswire publishes one **Portfolio audit update**. The linked immutable summary includes:

- the evaluation window and effective day;
- counts across all five statuses;
- average score and Positive, Neutral, or Negative overall direction;
- one row per held audited company;
- separate player, managed-fund, and combined share quantities;
- the company's audit score, adjusted return, dividend coverage, industry direction, and link to company details.

Ordinary news remains unchanged. A portfolio audit card is actionable only when it carries a stored summary identifier.
