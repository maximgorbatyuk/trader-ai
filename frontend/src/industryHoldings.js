const UNKNOWN_INDUSTRY = 'Unknown industry'

// Folds a participant's per-company holdings into per-industry buckets by joining each holding against the
// companies list (which carries the industry). Returned sorted by held value so the largest exposure leads.
export function groupHoldingsByIndustry(holdings, companies) {
  const industryByCompanyId = new Map(
    (companies ?? []).map((company) => [company.id, { id: company.industryId, name: company.industryName ?? UNKNOWN_INDUSTRY }]),
  )

  const buckets = new Map()
  for (const holding of holdings ?? []) {
    const industry = industryByCompanyId.get(holding.companyId) ?? { id: null, name: UNKNOWN_INDUSTRY }
    const key = industry.id ?? UNKNOWN_INDUSTRY
    const bucket = buckets.get(key) ?? { industryId: industry.id ?? null, industryName: industry.name, companyCount: 0, shares: 0, value: 0, costBasis: 0 }
    bucket.companyCount += 1
    bucket.shares += holding.shares
    bucket.value += holding.marketValue
    bucket.costBasis += holding.costBasis
    buckets.set(key, bucket)
  }

  const totalValue = [...buckets.values()].reduce((sum, bucket) => sum + bucket.value, 0)

  return [...buckets.values()]
    .map((bucket) => ({
      ...bucket,
      pnl: bucket.value - bucket.costBasis,
      pct: totalValue > 0 ? bucket.value / totalValue : 0,
    }))
    .sort((a, b) => b.value - a.value)
}
