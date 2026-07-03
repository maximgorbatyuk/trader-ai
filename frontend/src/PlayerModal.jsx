import { useCallback, useEffect, useRef, useState } from 'react'
import { api } from './api'
import { formatInt, formatMoney, formatSigned, toneOf } from './format'
import { CompanyCombobox } from './CompanyCombobox'

const POLL_INTERVAL_MS = 1000
const OPEN_STATUSES = new Set(['Open', 'PartiallyFilled'])
const CHANGE_GLYPH = { up: '▲', down: '▼', flat: '◆' }

// A signed money delta rendered with a market tone plus a glyph, so the sign never rides on colour alone;
// a non-numeric value (fewer than two worth snapshots) shows an em dash.
function ChangeAmount({ value }) {
  if (typeof value !== 'number') {
    return <span className="num tone-flat">—</span>
  }
  const tone = toneOf(value)
  return (
    <span className={`num tone-${tone}`}>
      <span aria-hidden="true">{CHANGE_GLYPH[tone]} </span>
      {formatSigned(value)}
    </span>
  )
}

// The player's live control surface: worth headline, balances, performance, holdings, the order form, and
// open orders. Owns its own polling so it can be dropped into either the dashboard tab or the modal.
export function PlayerPanel({ companies }) {
  const [loading, setLoading] = useState(true)
  const [player, setPlayer] = useState(null)
  const [holdings, setHoldings] = useState([])
  const [orders, setOrders] = useState([])
  const mountedRef = useRef(true)

  const refresh = useCallback(async () => {
    try {
      const playerData = await api.getPlayer()
      if (!mountedRef.current) return
      if (playerData) {
        const [holdingsData, orderData] = await Promise.all([
          api.getHoldings(playerData.id),
          api.getParticipantOrders(playerData.id, 20),
        ])
        if (!mountedRef.current) return
        setHoldings(holdingsData)
        setOrders(orderData)
      } else {
        setHoldings([])
        setOrders([])
      }
      setPlayer(playerData)
    } catch {
      // Keep the last known values when a refresh fails.
    } finally {
      if (mountedRef.current) setLoading(false)
    }
  }, [])

  useEffect(() => {
    mountedRef.current = true
    return () => {
      mountedRef.current = false
    }
  }, [])

  useEffect(() => {
    async function poll() {
      await refresh()
    }

    poll()
    const intervalId = setInterval(refresh, POLL_INTERVAL_MS)
    return () => clearInterval(intervalId)
  }, [refresh])

  // Headline delta tracks the last completed cycle; it stays hidden until the first cycle produces a figure.
  const lastCycleWorthChange = player?.lastCycleWorthChange
  const worthTone = typeof lastCycleWorthChange === 'number' ? toneOf(lastCycleWorthChange) : null

  return (
    <div className="player-panel">
      <div className="player-panel-head">
        <div className="command-id">
          <span className="command-label">Player</span>
          <span className="command-name">{player ? player.name : 'Play the market'}</span>
        </div>
        {player ? (
          <div className="quote">
            <strong className="quote-last num">{formatMoney(player.totalWorth)}</strong>
            {worthTone ? (
              <span className={`quote-change num tone-${worthTone}`} title="Change over the last completed cycle">
                <span aria-hidden="true">{CHANGE_GLYPH[worthTone]} </span>
                {formatSigned(lastCycleWorthChange)}
              </span>
            ) : null}
          </div>
        ) : null}
      </div>

      {loading ? (
        <p className="note">Loading the player…</p>
      ) : player === null ? (
        <JoinPanel onJoined={refresh} />
      ) : (
        <PlayerStats player={player} holdings={holdings} orders={orders} companies={companies} onRefresh={refresh} />
      )}
    </div>
  )
}

// The player's control panel as a modal opened from the top bar. It contributes only the dialog chrome
// (backdrop/Escape close, focus trap, scroll lock) and delegates all live content to PlayerPanel.
export function PlayerModal({ companies, onClose }) {
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
      'a[href], button:not([disabled]), input:not([disabled]), select:not([disabled]), [tabindex]:not([tabindex="-1"])',
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
        aria-label="Player"
        ref={dialogRef}
        onKeyDown={onDialogKeyDown}
      >
        <div className="modal-body">
          <PlayerPanel companies={companies} />
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

function JoinPanel({ onJoined }) {
  const [name, setName] = useState('')
  const [submitting, setSubmitting] = useState(false)
  const [error, setError] = useState(null)

  async function handleSubmit(event) {
    event.preventDefault()
    setError(null)
    setSubmitting(true)
    try {
      await api.createPlayer({ name: name.trim() || null })
      await onJoined()
    } catch (submitError) {
      setError(submitError.message)
    } finally {
      setSubmitting(false)
    }
  }

  return (
    <form className="modal-section player-section" onSubmit={handleSubmit}>
      <p className="note">
        Join the market as a human trader. You start with a random balance and place buy and sell orders through
        the same order book as everyone else. The market never touches your orders, so you cancel them yourself.
      </p>
      <label className="field">
        <span>Name</span>
        <input
          className="select"
          type="text"
          placeholder="Player"
          value={name}
          onChange={(event) => setName(event.target.value)}
        />
      </label>
      {error ? (
        <p className="command-error" role="alert">
          {error}
        </p>
      ) : null}
      <button type="submit" className="btn btn-primary" disabled={submitting}>
        {submitting ? 'Joining…' : 'Join the market'}
      </button>
    </form>
  )
}

function PlayerStats({ player, holdings, orders, companies, onRefresh }) {
  const openOrders = orders.filter((order) => OPEN_STATUSES.has(order.status))
  const lastCycleMissing = player.lastCycleMoneyChange == null || player.lastCycleWorthChange == null

  return (
    <>
      <div className="modal-section player-section">
        <span className="map-stat-label">Balances</span>
        <dl className="kv">
          <div className="kv-row">
            <dt>Initial balance</dt>
            <dd className="num">{formatMoney(player.initialBalance)}</dd>
          </div>
          <div className="kv-row">
            <dt>Current balance</dt>
            <dd className="num">{formatMoney(player.currentBalance)}</dd>
          </div>
          <div className="kv-row kv-sub">
            <dt>Available</dt>
            <dd className="num">{formatMoney(player.availableBalance)}</dd>
          </div>
          <div className="kv-row kv-sub">
            <dt>Reserved</dt>
            <dd className="num">{formatMoney(player.reservedBalance)}</dd>
          </div>
          <div className="kv-row kv-total">
            <dt>Total worth</dt>
            <dd className="num">{formatMoney(player.totalWorth)}</dd>
          </div>
        </dl>
      </div>

      <div className="modal-section player-section">
        <span className="map-stat-label">Performance</span>
        <table className="tbl">
          <thead>
            <tr>
              <th scope="col">Change</th>
              <th scope="col" className="ta-r">
                Overall
              </th>
              <th scope="col" className="ta-r">
                Last cycle
              </th>
            </tr>
          </thead>
          <tbody>
            <tr>
              <th scope="row">Money</th>
              <td className="ta-r">
                <ChangeAmount value={player.overallMoneyChange} />
              </td>
              <td className="ta-r">
                <ChangeAmount value={player.lastCycleMoneyChange} />
              </td>
            </tr>
            <tr>
              <th scope="row">Worth</th>
              <td className="ta-r">
                <ChangeAmount value={player.overallWorthChange} />
              </td>
              <td className="ta-r">
                <ChangeAmount value={player.lastCycleWorthChange} />
              </td>
            </tr>
          </tbody>
        </table>
        {lastCycleMissing ? (
          <p className="note note-sm">Last-cycle figures appear after one completed cycle.</p>
        ) : null}
      </div>

      <HoldingsSection holdings={holdings} />

      <PlaceOrderForm player={player} companies={companies} onPlaced={onRefresh} />

      <OpenOrdersSection orders={openOrders} companies={companies} onCancelled={onRefresh} />
    </>
  )
}

function HoldingsSection({ holdings }) {
  return (
    <div className="modal-section player-section">
      <span className="map-stat-label">Active assets</span>
      {holdings.length === 0 ? (
        <p className="note note-sm">No shares held yet.</p>
      ) : (
        <div className="tbl-scroll">
          <table className="tbl">
            <thead>
              <tr>
                <th scope="col">Company</th>
                <th scope="col" className="ta-r">
                  Shares
                </th>
                <th scope="col" className="ta-r">
                  Cost paid
                </th>
                <th scope="col" className="ta-r">
                  Value
                </th>
                <th scope="col" className="ta-r">
                  P/L
                </th>
              </tr>
            </thead>
            <tbody>
              {holdings.map((holding) => {
                const pnl = holding.marketValue - holding.costBasis
                return (
                  <tr key={holding.companyId}>
                    <th scope="row" className="cell-ellipsis">
                      {holding.companyName}
                    </th>
                    <td className="num ta-r">{formatInt(holding.shares)}</td>
                    <td className="num ta-r">{formatMoney(holding.costBasis)}</td>
                    <td className="num ta-r">{formatMoney(holding.marketValue)}</td>
                    <td className={`num ta-r tone-${toneOf(pnl)}`}>{formatSigned(pnl)}</td>
                  </tr>
                )
              })}
            </tbody>
          </table>
        </div>
      )}
    </div>
  )
}

function PlaceOrderForm({ player, companies, onPlaced }) {
  const [side, setSide] = useState('Buy')
  const [companyId, setCompanyId] = useState('')
  const [quantity, setQuantity] = useState('')
  const [limitPrice, setLimitPrice] = useState('')
  const [submitting, setSubmitting] = useState(false)
  const [error, setError] = useState(null)

  const resolvedCompanyId = companyId || companies[0]?.id || ''

  // Selecting a company seeds the limit price with its current share price, so the trader starts from the
  // live market quote instead of a blank field.
  function handleCompanyChange(id) {
    setCompanyId(String(id))
    const picked = companies.find((company) => String(company.id) === String(id))
    if (picked?.currentPrice != null) {
      setLimitPrice(picked.currentPrice.toFixed(2))
    }
  }

  async function handleSubmit(event) {
    event.preventDefault()
    setError(null)
    setSubmitting(true)
    try {
      await api.placeOrder({
        participantId: player.id,
        companyId: Number(resolvedCompanyId),
        type: side,
        quantity: Number(quantity),
        limitPrice: Number(limitPrice),
      })
      setQuantity('')
      setLimitPrice('')
      await onPlaced()
    } catch (submitError) {
      setError(submitError.message)
    } finally {
      setSubmitting(false)
    }
  }

  return (
    <form className="modal-section player-section" onSubmit={handleSubmit}>
      <span className="map-stat-label">Place order</span>
      <div className="field-pair">
        <label className="field">
          <span>Side</span>
          <select className="select" value={side} onChange={(event) => setSide(event.target.value)}>
            <option value="Buy">Buy ▲</option>
            <option value="Sell">Sell ▼</option>
          </select>
        </label>
        <div className="field">
          <span>Company</span>
          <CompanyCombobox companies={companies} value={resolvedCompanyId} onChange={handleCompanyChange} />
        </div>
      </div>
      <div className="field-pair">
        <label className="field">
          <span>Quantity</span>
          <input
            className="select num"
            type="number"
            min="1"
            step="1"
            placeholder="0"
            value={quantity}
            onChange={(event) => setQuantity(event.target.value)}
          />
        </label>
        <label className="field">
          <span>Limit price</span>
          <input
            className="select num"
            type="number"
            min="0.01"
            step="0.01"
            placeholder="0.00"
            value={limitPrice}
            onChange={(event) => setLimitPrice(event.target.value)}
          />
        </label>
      </div>
      {error ? (
        <p className="command-error" role="alert">
          {error}
        </p>
      ) : null}
      <button type="submit" className="btn btn-primary" disabled={submitting}>
        {submitting ? 'Placing…' : `Place ${side.toLowerCase()} order`}
      </button>
    </form>
  )
}

function OpenOrdersSection({ orders, companies, onCancelled }) {
  const [cancelingId, setCancelingId] = useState(null)
  const [error, setError] = useState(null)
  const companyById = new Map(companies.map((company) => [company.id, company]))

  async function handleCancel(orderId) {
    setError(null)
    setCancelingId(orderId)
    try {
      await api.cancelPlayerOrder(orderId)
      await onCancelled()
    } catch (cancelError) {
      setError(cancelError.message)
    } finally {
      setCancelingId(null)
    }
  }

  return (
    <div className="modal-section player-section">
      <span className="map-stat-label">Open orders</span>
      {error ? (
        <p className="command-error" role="alert">
          {error}
        </p>
      ) : null}
      {orders.length === 0 ? (
        <p className="note note-sm">No open orders.</p>
      ) : (
        <div className="tbl-scroll">
          <table className="tbl">
            <thead>
              <tr>
                <th scope="col">Side</th>
                <th scope="col">Company</th>
                <th scope="col" className="ta-r">
                  Filled
                </th>
                <th scope="col" className="ta-r">
                  Limit
                </th>
                <th scope="col" className="ta-r">
                  Market
                </th>
                <th scope="col" className="ta-r">
                  Action
                </th>
              </tr>
            </thead>
            <tbody>
              {orders.map((order) => {
                const company = companyById.get(order.companyId)
                return (
                <tr key={order.id}>
                  <td className={`tone-${order.type === 'Buy' ? 'up' : 'down'}`}>{order.type}</td>
                  <th scope="row" className="cell-ellipsis">
                    {company?.name ?? `#${order.companyId}`}
                  </th>
                  <td className="num ta-r">
                    {order.filledQuantity}
                    <span className="muted-sub">/{order.quantity}</span>
                  </td>
                  <td className="num ta-r">{formatMoney(order.limitPrice)}</td>
                  <td className="num ta-r">
                    {company?.currentPrice != null ? formatMoney(company.currentPrice) : '—'}
                  </td>
                  <td className="ta-r">
                    <button
                      type="button"
                      className="btn select-sm"
                      disabled={cancelingId === order.id}
                      onClick={() => handleCancel(order.id)}
                    >
                      {cancelingId === order.id ? 'Canceling…' : 'Cancel'}
                    </button>
                  </td>
                </tr>
                )
              })}
            </tbody>
          </table>
        </div>
      )}
    </div>
  )
}
