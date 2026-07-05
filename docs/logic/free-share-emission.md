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
- Recipients receive the shares at zero cost.
- The new shares are listed as a company sell order at a zero price.
- Emission forces no price change and cancels no existing orders; the added supply moves the price through normal trading.
- Each emission is announced as news with no price impact.
- The company page lists a company's emissions.

The emission logic lives in `ShareEmissionService`.
