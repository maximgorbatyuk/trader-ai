const NEWS_DIRECTION = {
  Increase: { tone: 'up', glyph: '▲', sign: '+' },
  Decrease: { tone: 'down', glyph: '▼', sign: '−' },
}

// A published event's market effect: none, or a signed percent move tied to a company or list of industries.
// When onSelectCompany is passed, a company-scoped target renders as a button that opens its modal.
export function NewsImpact({ post, onSelectCompany }) {
  if (post.scope === 'None' || !post.direction) {
    return <span className="news-impact news-impact-none">No market impact</span>
  }

  const direction = NEWS_DIRECTION[post.direction] ?? NEWS_DIRECTION.Increase
  const isCompany = post.scope === 'Company'
  const canClickCompany = isCompany && typeof onSelectCompany === 'function' && post.targetCompanyId != null
  const target = isCompany ? post.targetCompanyName ?? 'a company' : post.industryNames.join(', ')

  return (
    <span className={`news-impact num tone-${direction.tone}`}>
      <span aria-hidden="true">{direction.glyph} </span>
      {direction.sign}
      {Number(post.impactPercent ?? 0).toFixed(2)}%
      {target ? (
        <span className="news-impact-target">
          {' · '}
          {canClickCompany ? (
            <button
              type="button"
              className="link-btn"
              onClick={() => onSelectCompany(post.targetCompanyId)}
              title={`Open ${post.targetCompanyName} details`}
            >
              {post.targetCompanyName}
            </button>
          ) : (
            target
          )}
        </span>
      ) : null}
    </span>
  )
}
