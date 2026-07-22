# Free-share emission

Free-share emission lets a very large company issue new shares for free, diluting a runaway per-share price.

## Rules

- Only a company above a capitalisation threshold can emit.
- The chance to emit rises in steps as capitalisation grows, and is capped.
- A company that has just emitted waits through a long cooldown before it can emit again.
- Each emission mints a small random fraction of the current share count.
- New shares go only to active traders who do not already hold the company.
- No recipient receives more than a small fixed number of free shares.
- The amount actually emitted is limited by how many eligible recipients there are.
- The grant is recorded through a company-originated sell order with a zero limit that is filled in place. It never rests in the order book or participates in matching.
- Recipients receive settled holdings with a zero cost basis; no zero-price trade or price point is recorded.
- Emission forces no price change and cancels no existing orders. The larger issued supply and recipients' later sell decisions can weigh on price through ordinary matching.
- Each emission is announced as news with no price impact.
- The company page lists a company's emissions.

## Audit evidence

Every new emission keeps one recipient row per awarded trader with the exact granted quantity. The emission total and recipient count can therefore be reconciled to the individual grants without relying on current holdings, which may change after the event. Older emissions created before recipient evidence was introduced are not reconstructed from later portfolio state.
