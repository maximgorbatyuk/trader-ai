import { useEffect, useRef, useState } from 'react'
import { api } from './api'
import { CompanyCombobox } from './CompanyCombobox'

// Manual news composer: pick a target (one company or one/several industries), a theme for the wording, and
// the impact direction and percent. Submitting posts to the backend, which generates the headline and moves
// the affected prices.
export function AddNewsModal({ companies, onClose, onPublished }) {
  const [themes, setThemes] = useState([])
  const [industries, setIndustries] = useState([])
  const [scope, setScope] = useState('Company')
  const [themeKey, setThemeKey] = useState('')
  const [direction, setDirection] = useState('Increase')
  const [impactPercent, setImpactPercent] = useState('2')
  const [companyId, setCompanyId] = useState('')
  const [industryIds, setIndustryIds] = useState([])
  const [allIndustries, setAllIndustries] = useState(false)
  const [submitting, setSubmitting] = useState(false)
  const [error, setError] = useState(null)
  const dialogRef = useRef(null)

  useEffect(() => {
    let active = true
    Promise.all([api.getNewsThemes(), api.getIndustries()])
      .then(([themeData, industryData]) => {
        if (!active) return
        setThemes(themeData)
        setIndustries(industryData)
        setThemeKey((current) => current || themeData[0]?.key || '')
      })
      .catch(() => setError('Could not load themes and industries.'))
    return () => {
      active = false
    }
  }, [])

  useEffect(() => {
    function onKeyDown(event) {
      if (event.key === 'Escape') onClose()
    }
    document.addEventListener('keydown', onKeyDown)
    const previousOverflow = document.body.style.overflow
    document.body.style.overflow = 'hidden'
    return () => {
      document.removeEventListener('keydown', onKeyDown)
      document.body.style.overflow = previousOverflow
    }
  }, [onClose])

  const resolvedCompanyId = companyId || companies[0]?.id || ''

  function toggleIndustry(id) {
    setIndustryIds((current) => (current.includes(id) ? current.filter((value) => value !== id) : [...current, id]))
  }

  function onBackdropClick(event) {
    if (event.target === event.currentTarget) onClose()
  }

  async function handleSubmit(event) {
    event.preventDefault()
    setError(null)

    const payload = {
      scope,
      themeKey,
      direction,
      impactPercent: Number(impactPercent),
      targetCompanyId: scope === 'Company' ? Number(resolvedCompanyId) : null,
      industryIds:
        scope === 'Industries' ? (allIndustries ? industries.map((industry) => industry.id) : industryIds) : null,
    }

    setSubmitting(true)
    try {
      await api.createNews(payload)
      onPublished()
      onClose()
    } catch (submitError) {
      setError(submitError.message)
    } finally {
      setSubmitting(false)
    }
  }

  return (
    <div className="modal-backdrop" onClick={onBackdropClick}>
      <div className="modal" role="dialog" aria-modal="true" aria-label="Add news" ref={dialogRef}>
        <header className="modal-head">
          <div className="command-id">
            <span className="command-label">Newswire</span>
            <h2 className="command-name">Add news</h2>
          </div>
        </header>

        <form className="modal-body news-form" onSubmit={handleSubmit}>
          <label className="field">
            <span>Impacts</span>
            <select className="select" value={scope} onChange={(event) => setScope(event.target.value)} autoFocus>
              <option value="Company">A single company</option>
              <option value="Industries">Industries</option>
            </select>
          </label>

          {scope === 'Company' ? (
            <div className="field">
              <span>Company</span>
              <CompanyCombobox
                companies={companies}
                value={resolvedCompanyId}
                onChange={(id) => setCompanyId(String(id))}
              />
            </div>
          ) : (
            <div className="field">
              <span>Industries</span>
              <label className="industry-check industry-check-all">
                <input
                  type="checkbox"
                  checked={allIndustries}
                  onChange={(event) => setAllIndustries(event.target.checked)}
                />
                <span>All industries</span>
              </label>
              <div className="industry-picker" aria-disabled={allIndustries}>
                {industries.map((industry) => (
                  <label key={industry.id} className="industry-check">
                    <input
                      type="checkbox"
                      disabled={allIndustries}
                      checked={allIndustries || industryIds.includes(industry.id)}
                      onChange={() => toggleIndustry(industry.id)}
                    />
                    <span>{industry.name}</span>
                  </label>
                ))}
              </div>
            </div>
          )}

          <label className="field">
            <span>Theme</span>
            <select className="select" value={themeKey} onChange={(event) => setThemeKey(event.target.value)}>
              {themes.map((theme) => (
                <option key={theme.key} value={theme.key}>
                  {theme.label}
                </option>
              ))}
            </select>
          </label>

          <div className="field-pair">
            <label className="field">
              <span>Impact</span>
              <select className="select" value={direction} onChange={(event) => setDirection(event.target.value)}>
                <option value="Increase">Increase ▲</option>
                <option value="Decrease">Decrease ▼</option>
              </select>
            </label>
            <label className="field">
              <span>Percent</span>
              <input
                className="select num"
                type="number"
                min="0.1"
                max="95"
                step="0.1"
                value={impactPercent}
                onChange={(event) => setImpactPercent(event.target.value)}
              />
            </label>
          </div>

          {error ? (
            <p className="command-error" role="alert">
              {error}
            </p>
          ) : null}

          <footer className="modal-foot">
            <button type="button" className="btn" onClick={onClose}>
              Cancel
            </button>
            <button type="submit" className="btn btn-primary" disabled={submitting}>
              Publish news
            </button>
          </footer>
        </form>
      </div>
    </div>
  )
}
