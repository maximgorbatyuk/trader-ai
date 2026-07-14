import { useState } from 'react'
import { api } from './api'

export function FavoriteCompanyToggle({ companyId, companyName, isFavorite, onChanged }) {
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState(null)

  async function toggleFavorite() {
    const nextFavorite = !isFavorite
    setSaving(true)
    setError(null)
    try {
      if (nextFavorite) {
        await api.markFavoriteCompany(companyId)
      } else {
        await api.unmarkFavoriteCompany(companyId)
      }
      onChanged(nextFavorite)
    } catch (toggleError) {
      setError(toggleError.message)
    } finally {
      setSaving(false)
    }
  }

  const label = isFavorite ? 'Favorite company' : 'Mark as favorite'

  return (
    <div className="favorite-company-control">
      <button
        type="button"
        className={`btn favorite-company-toggle${isFavorite ? ' is-active' : ''}`}
        aria-pressed={isFavorite}
        aria-label={`${label}: ${companyName}`}
        disabled={saving}
        onClick={toggleFavorite}
      >
        <span className="favorite-company-glyph" aria-hidden="true">{isFavorite ? '★' : '☆'}</span>
        {saving ? 'Saving…' : label}
      </button>
      {error ? <span className="favorite-company-error" role="alert">{error}</span> : null}
    </div>
  )
}
