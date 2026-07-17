# Auditors

An Auditor is an independent rating agency. It reviews companies for risk, and its verdict is shown on the company page.

## Rules

- Auditors are created with the market, and are added automatically if a running market has none.
- The number of auditors is sized so about one company in twenty is reviewed each cycle.
- Each auditor reviews one company per cycle. A company is reviewed by at most one auditor per cycle.
- A larger company is more likely to be picked, but every eligible company keeps a minimum chance.
- A rated company is left alone for a safe period before it can be reviewed again.
- An issue-free review normally rates a stable company Low risk and a sharply moving company High risk.
- Sometimes an issue-free review instead raises expectations, lifting the share price by 5%–15% and recording a positive verdict.
- Extra risk has a 2% chance for a stable company and a 10% chance after a sharp move.
- A hidden issue raises the verdict to Extra risk and drops the share price by 10%–20%.
- An equally likely Extra raised expectations verdict can occupy the positive side of that roll when the company's latest five audits contain no Extra risk and at least one ordinary raised expectations verdict.
- Extra raised expectations lifts the share price by 10%–20%. An older Extra risk outside the latest five audits does not block it, and an older ordinary raise outside that window does not enable it.
- An Extra raised-expectations verdict also creates a one-cycle opportunity for an eligible automated participant
  or fund to make a big investment in the company on the following cycle. More settled, unreserved cash raises the
  selected investor's chance, with a hard maximum of 50%.
- The larger a company's share of total market capitalization, the narrower its Extra raised expectations band, so a market giant is handed this exceptional raise far less often; a company that is nearly the whole market keeps only a small residual chance.
- An active crisis triples both Extra-outcome chances.
- The Extra-risk price drop uses the normal news-impact mechanism and is announced as company news.
- A High-risk verdict is announced as news with no price impact.
- A Low-risk verdict is recorded but posts no news.
- When a company is rated High or Extra risk, holders of open buy orders on it may cancel them.
- The cancellation chance is higher for Extra risk, and shifts with the buyer's risk profile and temperament.
- The market never cancels the human player's orders this way.
- Both raised-expectations verdicts cancel open participant sell orders so owners can re-list around the higher price, except orders owned by the human player or a bankrupt participant.
- Both raised-expectations verdicts are positive news and are not added to an active crisis timeline.
- The company page shows the current rating and how it changed. Each auditor has a page listing the companies it has reviewed.

The auditing logic lives in `AuditorService`.
