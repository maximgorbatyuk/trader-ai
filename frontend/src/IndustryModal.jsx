import { useEffect, useRef } from 'react'
import { Link } from 'react-router-dom'
import { formatCompactMoney, formatInt, toneOf } from './format'
import { formatPct } from './treemapLayout'

const CHANGE_GLYPH = { up: '▲', down: '▼', flat: '–' }

// Companies of one industry, opened from the Industries table or a treemap tile. The list is not paginated
// but scrolls inside the dialog; each company name links to its own page. Capitalization change is the same
// forward-only cycle diff the industries treemap uses, so it is 0 until a cycle advances after load.
export function IndustryModal({ industry, companies, companyChangeById, onClose }) {
  const dialogRef = useRef(null)
  const closeRef = useRef(null)

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

  useEffect(() => {
    const previouslyFocused = document.activeElement
    closeRef.current?.focus()
    return () => {
      if (previouslyFocused instanceof HTMLElement) previouslyFocused.focus()
    }
  }, [])

  if (!industry) {
    return null
  }

  const rows = companies
    .filter((company) => company.industryId === industry.id)
    .map((company) => ({
      ...company,
      capitalization: company.issuedSharesCount * (company.currentPrice ?? 0),
      changePct: companyChangeById.get(company.id) ?? 0,
    }))
    .sort((a, b) => b.capitalization - a.capitalization)

  const titleId = `industry-modal-title-${industry.id}`

  function onBackdropClick(event) {
    if (event.target === event.currentTarget) {
      onClose()
    }
  }

  function onDialogKeyDown(event) {
    if (event.key !== 'Tab') {
      return
    }

    const focusable = dialogRef.current?.querySelectorAll(
      'a[href], button:not([disabled]), [tabindex]:not([tabindex="-1"])',
    )
    if (!focusable || focusable.length === 0) {
      return
    }

    const first = focusable[0]
    const lastFocusable = focusable[focusable.length - 1]
    if (event.shiftKey && document.activeElement === first) {
      event.preventDefault()
      lastFocusable.focus()
    } else if (!event.shiftKey && document.activeElement === lastFocusable) {
      event.preventDefault()
      first.focus()
    }
  }

  return (
    <div className="modal-backdrop" onClick={onBackdropClick}>
      <div
        className="modal modal-company"
        role="dialog"
        aria-modal="true"
        aria-labelledby={titleId}
        ref={dialogRef}
        onKeyDown={onDialogKeyDown}
      >
        <header className="modal-head">
          <div className="command-id">
            <span className="command-label">Industry</span>
            <h2 className="command-name" id={titleId}>
              {industry.name}
            </h2>
          </div>
          <span className="pill pill-muted num">{formatInt(rows.length)} companies</span>
        </header>

        <div className="modal-body">
          {rows.length === 0 ? (
            <p className="note">No companies in this industry.</p>
          ) : (
            <div className="tbl-scroll">
              <table className="tbl">
                <thead>
                  <tr>
                    <th scope="col">Name</th>
                    <th scope="col" className="ta-r">
                      Shares
                    </th>
                    <th scope="col" className="ta-r">
                      Capitalization
                    </th>
                  </tr>
                </thead>
                <tbody>
                  {rows.map((company) => {
                    const tone = toneOf(company.changePct)
                    return (
                      <tr key={company.id}>
                        <th scope="row" className="cell-ellipsis">
                          <Link className="cell-link" to={`/companies/${company.id}`} onClick={onClose}>
                            {company.name}
                          </Link>
                        </th>
                        <td className="num ta-r">{formatInt(company.issuedSharesCount)}</td>
                        <td className="num ta-r">
                          {formatCompactMoney(company.capitalization)}
                          <span className={`book-diff num tone-${tone}`}>
                            <span aria-hidden="true">{CHANGE_GLYPH[tone]} </span>
                            {formatPct(company.changePct)}
                          </span>
                        </td>
                      </tr>
                    )
                  })}
                </tbody>
              </table>
            </div>
          )}
        </div>

        <footer className="modal-foot">
          <button type="button" className="btn" ref={closeRef} onClick={onClose}>
            Close
          </button>
        </footer>
      </div>
    </div>
  )
}
