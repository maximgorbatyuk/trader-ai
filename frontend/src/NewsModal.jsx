import { useEffect, useRef } from 'react'
import { formatInt } from './format'
import { NewsImpact } from './NewsImpact'

// Full detail for one news post: headline, body, its market impact, and the industries (or company) it hit.
// Opened from the News page table and from the related-news blocks on the company views.
export function NewsModal({ post, onClose }) {
  const dialogRef = useRef(null)
  const closeRef = useRef(null)

  // Close on Escape and lock background scroll while the dialog is open.
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

  // Move focus into the dialog on open and restore it to the trigger on close.
  useEffect(() => {
    const previouslyFocused = document.activeElement
    closeRef.current?.focus()
    return () => {
      if (previouslyFocused instanceof HTMLElement) previouslyFocused.focus()
    }
  }, [])

  if (!post) {
    return null
  }

  const titleId = `news-modal-title-${post.id}`
  const industries = post.industryNames ?? []

  function onBackdropClick(event) {
    if (event.target === event.currentTarget) {
      onClose()
    }
  }

  // Keep Tab focus inside the dialog by wrapping it at the first and last focusable controls.
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
        className="modal"
        role="dialog"
        aria-modal="true"
        aria-labelledby={titleId}
        ref={dialogRef}
        onKeyDown={onDialogKeyDown}
      >
        <header className="modal-head">
          <div className="command-id">
            <span className="command-label">Newswire</span>
            <h2 className="command-name" id={titleId}>
              {post.title}
            </h2>
          </div>
          <NewsImpact post={post} />
        </header>

        <div className="modal-body">
          <p className="news-body">{post.content}</p>

          <dl className="modal-stats">
            <div>
              <dt>Impact scope</dt>
              <dd>{post.scope === 'None' ? 'No market impact' : post.scope}</dd>
            </div>
            {post.scope === 'Company' && post.targetCompanyName ? (
              <div>
                <dt>Company</dt>
                <dd className="cell-ellipsis">{post.targetCompanyName}</dd>
              </div>
            ) : null}
            <div>
              <dt>Published</dt>
              <dd className="num">cycle {formatInt(post.publishedInCycleNumber)}</dd>
            </div>
          </dl>

          <div className="modal-section">
            <span className="map-stat-label">Industries impacted</span>
            {industries.length > 0 ? (
              <ul className="chip-list">
                {industries.map((name) => (
                  <li key={name} className="tag">
                    {name}
                  </li>
                ))}
              </ul>
            ) : (
              <p className="note note-sm">
                {post.scope === 'Company' ? 'This post targets a single company, not an industry.' : 'No industries were affected.'}
              </p>
            )}
          </div>
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
