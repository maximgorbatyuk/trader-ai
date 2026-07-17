import { useEffect, useRef, useState } from 'react'
import { api } from './api'
import { formatMoney } from './format'
import { Modal } from './Modal'
import { parseCashAdjustment, transferableSettledCash } from './participantActionsModel'

export function ParticipantActions({ participant, onChanged }) {
  const [menuOpen, setMenuOpen] = useState(false)
  const [action, setAction] = useState(null)
  const [notice, setNotice] = useState(null)
  const buttonRef = useRef(null)
  const menuRef = useRef(null)

  useEffect(() => {
    if (!menuOpen) return undefined
    function onPointerDown(event) {
      if (buttonRef.current?.contains(event.target) || menuRef.current?.contains(event.target)) return
      setMenuOpen(false)
    }
    function onKeyDown(event) {
      if (event.key === 'Escape') {
        setMenuOpen(false)
        buttonRef.current?.focus()
      }
    }
    document.addEventListener('mousedown', onPointerDown)
    document.addEventListener('keydown', onKeyDown)
    return () => {
      document.removeEventListener('mousedown', onPointerDown)
      document.removeEventListener('keydown', onKeyDown)
    }
  }, [menuOpen])

  function choose(nextAction) {
    setMenuOpen(false)
    setNotice(null)
    setAction(nextAction)
  }

  async function completed(message) {
    setAction(null)
    setNotice(message)
    await onChanged?.()
  }

  return (
    <div className="participant-actions">
      <div className="command-actions">
        <button
          ref={buttonRef}
          type="button"
          className="btn actions-toggle"
          aria-expanded={menuOpen}
          onClick={() => setMenuOpen((current) => !current)}
        >
          Actions
          <span className="actions-caret" aria-hidden="true">▾</span>
        </button>
        {menuOpen ? (
          <div className="actions-menu" role="group" ref={menuRef} aria-label="Trader actions">
            <button type="button" className="actions-menu-item" onClick={() => choose('rename')}>
              Rename
            </button>
            <button type="button" className="actions-menu-item" onClick={() => choose('cash')}>
              Adjust cash
            </button>
            <button
              type="button"
              className="actions-menu-item actions-menu-danger"
              disabled={participant.memberOfCollectiveFundId == null}
              onClick={() => choose('leave')}
            >
              Force to leave Fund
            </button>
          </div>
        ) : null}
      </div>

      {notice ? <p className="participant-action-status" role="status">{notice}</p> : null}

      {action === 'rename' ? (
        <RenameDialog participant={participant} onClose={() => setAction(null)} onCompleted={completed} />
      ) : null}
      {action === 'cash' ? (
        <CashAdjustmentDialog participant={participant} onClose={() => setAction(null)} onCompleted={completed} />
      ) : null}
      {action === 'leave' ? (
        <ForceLeaveDialog participant={participant} onClose={() => setAction(null)} onCompleted={completed} />
      ) : null}
    </div>
  )
}

function RenameDialog({ participant, onClose, onCompleted }) {
  const [name, setName] = useState(participant.name)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState(null)
  const titleId = `rename-participant-${participant.id}`

  async function submit(event) {
    event.preventDefault()
    const normalized = name.trim()
    if (!normalized) return
    setBusy(true)
    setError(null)
    try {
      await api.renameParticipant(participant.id, normalized)
      await onCompleted('Trader renamed.')
    } catch (submitError) {
      setError(submitError.message)
    } finally {
      setBusy(false)
    }
  }

  return (
    <ActionModal titleId={titleId} label="Trader action" title={`Rename ${participant.name}`} onClose={onClose}>
      <form className="modal-section player-section" onSubmit={submit}>
        <label className="field">
          <span>Name</span>
          <input className="select" value={name} onChange={(event) => setName(event.target.value)} autoFocus />
        </label>
        <ActionError error={error} />
        <div className="order-actions">
          <button type="button" className="btn" onClick={onClose}>Cancel</button>
          <button type="submit" className="btn btn-primary" disabled={busy || !name.trim()}>
            {busy ? 'Renaming…' : 'Rename'}
          </button>
        </div>
      </form>
    </ActionModal>
  )
}

function CashAdjustmentDialog({ participant, onClose, onCompleted }) {
  const [amount, setAmount] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState(null)
  const titleId = `adjust-participant-cash-${participant.id}`
  const parsedAmount = parseCashAdjustment(amount)
  const removable = transferableSettledCash(participant)

  async function submit(event) {
    event.preventDefault()
    if (parsedAmount == null) return
    setBusy(true)
    setError(null)
    try {
      await api.adjustParticipantCash(participant.id, parsedAmount)
      await onCompleted('Settled cash adjusted.')
    } catch (submitError) {
      setError(submitError.message)
    } finally {
      setBusy(false)
    }
  }

  return (
    <ActionModal titleId={titleId} label="Trader action" title={`Adjust ${participant.name} cash`} onClose={onClose}>
      <form className="modal-section player-section" onSubmit={submit}>
        <label className="field">
          <span>Signed adjustment</span>
          <input
            className="select num"
            type="number"
            step="0.01"
            placeholder="Positive adds · negative removes"
            value={amount}
            onChange={(event) => setAmount(event.target.value)}
            autoFocus
          />
        </label>
        <p className="note note-sm">
          The adjustment is immediately settled and leaves unsettled cash unchanged. Up to {formatMoney(removable)} can be removed.
        </p>
        <ActionError error={error} />
        <div className="order-actions">
          <button type="button" className="btn" onClick={onClose}>Cancel</button>
          <button type="submit" className="btn btn-primary" disabled={busy || parsedAmount == null}>
            {busy ? 'Adjusting…' : 'Adjust cash'}
          </button>
        </div>
      </form>
    </ActionModal>
  )
}

function ForceLeaveDialog({ participant, onClose, onCompleted }) {
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState(null)
  const titleId = `force-participant-leave-${participant.id}`

  async function submit() {
    setBusy(true)
    setError(null)
    try {
      const result = await api.forceParticipantLeaveFund(participant.id)
      const message = result.status === 'Left'
        ? 'Trader left the fund.'
        : result.status === 'FundClosing'
          ? 'Fund closing started.'
          : 'Fund withdrawal is pending.'
      await onCompleted(message)
    } catch (submitError) {
      setError(submitError.message)
    } finally {
      setBusy(false)
    }
  }

  return (
    <ActionModal titleId={titleId} label="Trader action" title={`Force ${participant.name} to leave Fund`} onClose={onClose}>
      <div className="modal-section player-section">
        <p className="note note-sm">
          This bypasses the normal tenure, probability, and daily withdrawal limits for {participant.memberOfCollectiveFundName ?? 'the current fund'}.
        </p>
        <ActionError error={error} />
        <div className="order-actions">
          <button type="button" className="btn" onClick={onClose}>Cancel</button>
          <button type="button" className="btn btn-danger" disabled={busy} onClick={submit}>
            {busy ? 'Forcing withdrawal…' : 'Force to leave Fund'}
          </button>
        </div>
      </div>
    </ActionModal>
  )
}

function ActionModal({ titleId, label, title, onClose, children }) {
  return (
    <Modal titleId={titleId} className="modal-action" onClose={onClose}>
      <header className="modal-head">
        <div className="command-id">
          <span className="command-label">{label}</span>
          <h2 className="command-name" id={titleId}>{title}</h2>
        </div>
        <button type="button" className="btn" onClick={onClose}>Close</button>
      </header>
      <div className="modal-body">{children}</div>
    </Modal>
  )
}

function ActionError({ error }) {
  return error ? <p className="command-error" role="alert">{error}</p> : null
}
