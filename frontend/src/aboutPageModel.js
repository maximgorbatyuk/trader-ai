export const ABOUT_DOCUMENTS = [
  { key: 'domain', label: 'Domain', sourcePath: 'domain.md' },
  { key: 'participant-rules', label: 'Participant rules', sourcePath: 'participant-rules.md' },
  { key: 'individual', label: 'Individual', sourcePath: 'roles/individual.md' },
  { key: 'ai-agent', label: 'AI Agent', sourcePath: 'roles/ai-agent.md' },
  { key: 'player', label: 'Player', sourcePath: 'roles/player.md' },
  { key: 'company', label: 'Company', sourcePath: 'roles/company.md' },
  { key: 'collective-fund', label: 'Collective Fund', sourcePath: 'roles/collective-fund.md' },
  { key: 'fund-member', label: 'Fund Member', sourcePath: 'roles/fund-member.md' },
  { key: 'auditors', label: 'Auditors', sourcePath: 'roles/auditors.md' },
  { key: 'share-price-formation', label: 'Share price formation', sourcePath: 'rules/share-price-formation.md' },
  { key: 'trading-days', label: 'Trading days', sourcePath: 'rules/trading-days.md' },
  { key: 'luld', label: 'LULD', sourcePath: 'rules/luld.md' },
  { key: 'settlement', label: 'Settlement', sourcePath: 'logic/settlement.md' },
  { key: 'margin', label: 'Margin accounts', sourcePath: 'logic/margin.md' },
  { key: 'crisis', label: 'Market crises', sourcePath: 'logic/crisis.md' },
  { key: 'corporate-cash', label: 'Corporate cash', sourcePath: 'logic/corporate-cash.md' },
  { key: 'sector-sentiment', label: 'Sector sentiment', sourcePath: 'logic/sector-sentiment.md' },
  { key: 'free-share-emission', label: 'Free-share emission', sourcePath: 'logic/free-share-emission.md' },
  { key: 'big-investment', label: 'Big investment', sourcePath: 'logic/big-investment.md' },
  { key: 'bank-loans', label: 'Bank loans', sourcePath: 'logic/bank-loans.md' },
  { key: 'fund-advertising', label: 'Fund advertising', sourcePath: 'logic/fund-advertising.md' },
  { key: 'behavioral-audit', label: 'Behavioural audit', sourcePath: 'logic/behavioral-audit.md' },
]

const DOCUMENT_BY_PATH = new Map(ABOUT_DOCUMENTS.map((document) => [document.sourcePath, document]))

export function aboutDocumentKeyForHref(href, currentPath) {
  if (!href || href.startsWith('#')) return null

  try {
    const resolved = new URL(href, `https://about.local/docs/${currentPath}`)
    if (resolved.origin !== 'https://about.local') return null
    return DOCUMENT_BY_PATH.get(resolved.pathname.replace(/^\/docs\//, ''))?.key ?? null
  } catch {
    return null
  }
}

export function aboutTabKeyAfterKeyDown(activeKey, eventKey) {
  const index = ABOUT_DOCUMENTS.findIndex((document) => document.key === activeKey)
  if (index < 0) return null

  if (eventKey === 'ArrowRight' || eventKey === 'ArrowLeft') {
    const step = eventKey === 'ArrowRight' ? 1 : -1
    return ABOUT_DOCUMENTS[(index + step + ABOUT_DOCUMENTS.length) % ABOUT_DOCUMENTS.length].key
  }
  if (eventKey === 'Home') return ABOUT_DOCUMENTS[0].key
  if (eventKey === 'End') return ABOUT_DOCUMENTS[ABOUT_DOCUMENTS.length - 1].key
  return null
}
