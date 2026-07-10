# Sector sentiment

Sector sentiment is the market's slow-moving confidence measure for each industry. A positive reading means that sector is in favour; a negative reading means investors are more cautious about it. It adds sector rotation to the normal company-by-company trading signals without replacing them.

## Rules

- Each sector begins with its own mood, volatility, and a fixed SectorBeta.
- Mood changes in small steps, drifts back toward neutral when there is no fresh evidence, and stays within a fixed range. More volatile sectors are more likely to move.
- A crisis creates broad risk-off pressure and adds a further negative push to the sectors it strikes.
- A science investigation can improve confidence in the selected sectors.
- Positive and negative news moves the confidence of the sectors it covers. A company headline also becomes evidence for its company's sector on the following decision cycle.
- SectorBeta is the sector's structural shock sensitivity: defensive sectors below one soften a shock, while cyclical sectors above one amplify it.
- The shock adjustment applies that beta and the current, bounded mood to crisis, science, and scoped-news price shocks only. A favourable mood cushions a negative shock and reinforces a positive one; an unfavourable mood does the reverse. Neutral mood still leaves the sector's beta in effect. Ordinary order matching and unrelated market mechanics do not use this adjustment.
- The same mood also shapes automated-trader demand: favourable sectors gain a modest buy pull, while shunned sectors gain a modest sell pull.
- A scoped news price effect lands after matching at the cycle boundary. It therefore cannot trade against the already-settled order book and is available to the next decision cycle.
- Sentiment history is retained in the live market for the same rolling window as other market histories, 500 cycles by default. Older history is archived so the running simulation stays focused on current data.

The Industries page and each industry's detail page show the current mood, its recent change, and the retained history.
