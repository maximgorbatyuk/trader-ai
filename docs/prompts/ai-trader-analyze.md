Act as a senior quantitative trading analyst and database investigator.

Analyze the database file at: <DATABASE_PATH>

Work read-only. Do not modify the database, application code, or repository files.

Primary goal:
Identify all AI-controlled traders, reconstruct and evaluate their trading decisions, and determine:

1. Which AI trader makes the most accurate predictions?
2. Which AI trader generates the most money?
3. Which AI trader achieves the highest net worth?
4. Which AI trader performs best after adjusting for capital, risk, fees, and sample size?

Phase 1 — Understand and validate the data

- Detect the database type and inspect its complete schema.
- Identify tables containing traders, AI agents/models, predictions, orders, trades, positions, balances, market prices, fees, deposits, and withdrawals.
- Determine how AI traders can be distinguished from human or system traders.
- Do not assume that a trader is AI-controlled without supporting fields or relationships.
- Explain the identification criteria used.
- Determine the available date range, assets, currencies, prediction horizons, and market coverage.
- Check for missing records, duplicates, inconsistent timestamps, canceled orders, incomplete trades, stale prices, and other data-quality problems.
- Clearly state which requested metrics cannot be calculated reliably.

Phase 2 — Reconstruct decisions

For every AI trader, reconstruct the sequence of decisions where supported by the data:

- Prediction time and prediction horizon
- Asset or market
- Predicted direction, price, return, or signal
- Confidence or probability, if available
- Buy, sell, hold, long, short, entry, exit, and position-sizing decisions
- Order execution status and execution price
- Position opened or closed
- Market outcome after the prediction horizon
- Realized and unrealized profit or loss
- Fees, commissions, funding costs, and stored slippage
- Capital and portfolio value before and after the decision

Do not treat an unexecuted prediction as a trade. Do not treat an open position as a realized win or loss.

Phase 3 — Evaluate prediction quality

Measure prediction performance separately from trading performance.

Where the required data exists, calculate:

- Number of predictions
- Directional accuracy
- Precision for bullish and bearish predictions
- Accuracy by asset and prediction horizon
- Accuracy by confidence level
- Brier score or log loss when probability predictions are available
- Average price or return prediction error
- Profit-weighted accuracy
- Performance by market regime or time period
- Statistical uncertainty or confidence intervals

Guard against look-ahead bias. Only use market information that became available after the recorded prediction and at the appropriate prediction horizon.

Do not declare a trader superior based only on raw accuracy. Consider sample size, confidence calibration, prediction difficulty, and consistency.

Phase 4 — Evaluate financial performance

For every AI trader, calculate when supported:

- Starting capital
- Deposits and withdrawals
- Gross realized P&L
- Net realized P&L after recorded costs
- Unrealized P&L
- Total P&L
- Absolute money generated
- Percentage return
- Ending cash balance
- Ending portfolio net worth
- Win rate
- Average win and average loss
- Profit factor
- Expectancy per trade
- Maximum drawdown
- Volatility
- Sharpe and Sortino ratios, if the data frequency is sufficient
- Return on allocated capital
- P&L per trade and per unit of risk
- Exposure, turnover, and number of completed trades

Keep deposits and withdrawals separate from trading profit.

Define ending net worth as cash plus the marked-to-market value of open positions at the latest reliable price. If net worth must be reconstructed rather than read from a trustworthy snapshot, label it as an estimate and explain the valuation method.

Do not invent fees, slippage, exchange rates, or missing market prices. If multiple currencies exist and reliable conversion rates are unavailable, report them separately.

Phase 5 — Make fair comparisons

Compare traders over both:

- Their complete available history
- A common overlapping period, if they operated during different date ranges

Where possible, also normalize comparisons by:

- Starting capital
- Average allocated capital
- Position size
- Risk taken
- Number of predictions
- Number of completed trades
- Assets traded
- Market regime

Highlight traders whose apparent performance is driven by unusually high capital, leverage, concentration, one exceptional trade, or a very small sample.

Phase 6 — Analyze decision quality

For each AI trader, summarize:

- Main trading behavior and strategy patterns
- Strengths and weaknesses
- Assets and market conditions where it performs best or worst
- Whether confidence is aligned with actual success
- Position-sizing discipline
- Entry and exit quality
- Risk-management behavior
- Repeated mistakes
- Best and worst decisions, with dates and supporting numbers
- Whether good predictions were converted into profitable trades
- Whether profits came from skill, risk-taking, starting capital, or isolated outliers

Final output

Provide:

1. Executive summary
2. Data sources, coverage, and AI-trader identification criteria
3. Data-quality findings
4. Prediction-quality leaderboard
5. Profitability leaderboard
6. Net-worth leaderboard
7. Risk-adjusted performance leaderboard
8. Individual analysis of every AI trader
9. Examples of the most important good and bad decisions
10. Final verdict and limitations

Use a comparison table containing at least:

- Trader
- AI model or strategy
- Active period
- Prediction count
- Completed trade count
- Prediction accuracy
- Net realized P&L
- Total P&L
- Return percentage
- Maximum drawdown
- Profit factor
- Risk-adjusted return
- Ending net worth
- Confidence level in the conclusion

Answer the following questions independently:

- Best predictor: who has the strongest prediction quality and why?
- Most profitable: who generated the largest net trading profit and why?
- Highest net worth: who has the largest ending portfolio value and how much came from starting capital or deposits?
- Best overall trader: who provides the strongest combination of prediction quality, profitability, consistency, and controlled risk?

If different traders win different categories, say so explicitly. Do not force a single winner.

Show metric definitions and important assumptions. Support every conclusion with calculated evidence. Distinguish confirmed findings from estimates and hypotheses.
