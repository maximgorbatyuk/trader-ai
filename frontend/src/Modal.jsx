import { useEffect, useRef } from 'react'

// Dismissal stays configurable because required setup flows must retain the shared focus trap and scroll lock
// without allowing Escape or backdrop clicks to bypass completion.
export function Modal({ titleId, className, onClose, children, dismissible = true }) {
  const dialogRef = useRef(null)

  useEffect(() => {
    function onKeyDown(event) {
      if (dismissible && event.key === 'Escape') onClose?.()
    }

    if (dismissible) document.addEventListener('keydown', onKeyDown)
    const previousOverflow = document.body.style.overflow
    document.body.style.overflow = 'hidden'
    return () => {
      if (dismissible) document.removeEventListener('keydown', onKeyDown)
      document.body.style.overflow = previousOverflow
    }
  }, [dismissible, onClose])

  // Move focus into the dialog on open and restore it to the trigger on close.
  useEffect(() => {
    const previouslyFocused = document.activeElement
    dialogRef.current?.focus()
    return () => {
      if (previouslyFocused instanceof HTMLElement) previouslyFocused.focus()
    }
  }, [])

  function onBackdropClick(event) {
    if (dismissible && event.target === event.currentTarget) {
      onClose?.()
    }
  }

  // Keep Tab focus inside the dialog by wrapping it at the first and last focusable controls.
  function onDialogKeyDown(event) {
    if (event.key !== 'Tab') {
      return
    }

    const focusable = dialogRef.current?.querySelectorAll(
      'a[href], button:not([disabled]), input:not([disabled]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"])',
    )
    if (!focusable || focusable.length === 0) {
      return
    }

    const first = focusable[0]
    const last = focusable[focusable.length - 1]
    if (event.shiftKey && document.activeElement === first) {
      event.preventDefault()
      last.focus()
    } else if (!event.shiftKey && document.activeElement === last) {
      event.preventDefault()
      first.focus()
    }
  }

  return (
    <div
      className="modal-backdrop"
      data-dismissible={dismissible}
      onClick={dismissible ? onBackdropClick : undefined}
    >
      <div
        className={`modal${className ? ` ${className}` : ''}`}
        role="dialog"
        aria-modal="true"
        aria-labelledby={titleId}
        ref={dialogRef}
        tabIndex={-1}
        onKeyDown={onDialogKeyDown}
      >
        {children}
      </div>
    </div>
  )
}
