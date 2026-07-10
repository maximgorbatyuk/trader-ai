import { useState } from 'react'
import { api } from './api'
import { formatInt, formatMoney } from './format'
import { PercentButtons } from './PercentButtons'

const QUANTITY_PRESETS = [
  { label: '10%', value: 0.1 },
  { label: '25%', value: 0.25 },
  { label: '50%', value: 0.5 },
  { label: '75%', value: 0.75 },
  { label: '100%', value: 1 },
]

const PRICE_PRESETS = [
  { label: '−25%', value: -0.25 },
  { label: '−10%', value: -0.1 },
  { label: 'Original', value: 0 },
  { label: '+10%', value: 0.1 },
  { label: '+25%', value: 0.25 },
]

// Order entry for one company and side, scoped so only quantity and limit price are asked (the limit defaults to
// the current price). Placing as the player is always offered; when the player also runs a fund, a second submit
// places the same order through the fund's cash and holdings. Sells cap each actor at the shares it owns; buys
// leave cash to the server to validate, since a buy may draw on margin.
export function OrderForm({ player, fund, company, side, playerMaxQuantity, fundMaxQuantity, onPlaced }) {
  const [quantity, setQuantity] = useState('')
  const [limitPrice, setLimitPrice] = useState(company.currentPrice != null ? String(company.currentPrice) : '')
  const [submittingActor, setSubmittingActor] = useState(null)
  const [error, setError] = useState(null)
  const [confirmation, setConfirmation] = useState(null)

  const isSell = side === 'Sell'

  const actors = [
    { key: 'player', label: 'Player', id: player.id, balance: player.availableBalance ?? 0, owned: playerMaxQuantity ?? 0 },
  ]
  if (fund) {
    actors.push({ key: 'fund', label: 'Fund', id: fund.id, balance: fund.availableBalance ?? 0, owned: fundMaxQuantity ?? 0 })
  }
  const hasFund = actors.length > 1

  function affordable(balance) {
    const price = Number(limitPrice)
    return price > 0 ? Math.floor(balance / price) : 0
  }

  // Presets size against the player: sells at the player's holding, buys at what the player's cash covers.
  function pickQuantity(fraction) {
    const max = isSell ? actors[0].owned : affordable(actors[0].balance)
    setQuantity(String(Math.ceil(max * fraction)))
  }

  // Price presets nudge the limit off the company's current price; "Original" (value 0) snaps back to it.
  function pickPrice(delta) {
    if (company.currentPrice == null) return
    setLimitPrice(String(Math.round(company.currentPrice * (1 + delta) * 100) / 100))
  }

  function disabledFor(actor) {
    const qty = Number(quantity)
    const price = Number(limitPrice)
    if (submittingActor != null || !(qty > 0) || !(price > 0)) return true
    // Selling more than an actor owns is always invalid; buys may draw on margin, so cash is not gated here.
    if (isSell && qty > actor.owned) return true
    return false
  }

  async function submitFor(actor) {
    setError(null)
    setConfirmation(null)
    setSubmittingActor(actor.key)
    try {
      await api.placeOrder({
        participantId: actor.id,
        companyId: company.id,
        type: side,
        quantity: Number(quantity),
        limitPrice: Number(limitPrice),
      })
      const who = hasFund ? `${actor.label} ` : ''
      setConfirmation(`${who}${side.toLowerCase()} order placed: ${formatInt(Number(quantity))} @ ${formatMoney(Number(limitPrice))}.`)
      setQuantity('')
      if (onPlaced) await onPlaced()
    } catch (submitError) {
      setError(submitError.message)
    } finally {
      setSubmittingActor(null)
    }
  }

  function onFormSubmit(event) {
    event.preventDefault()
    if (!disabledFor(actors[0])) submitFor(actors[0])
  }

  return (
    <form className="modal-section player-section" onSubmit={onFormSubmit}>
      <span className="map-stat-label">{isSell ? 'Sell shares' : 'Buy shares'}</span>
      <div className="field-pair">
        <div className="field">
          <span>Quantity</span>
          <PercentButtons options={QUANTITY_PRESETS} ariaLabel="Set quantity from a percentage" onPick={pickQuantity} />
          <input
            className="select num"
            type="number"
            min="1"
            step="1"
            placeholder="0"
            aria-label={isSell ? 'Sell quantity' : 'Buy quantity'}
            value={quantity}
            onChange={(event) => setQuantity(event.target.value)}
          />
        </div>
        <div className="field">
          <span>Limit price</span>
          {company.currentPrice != null ? (
            <PercentButtons options={PRICE_PRESETS} ariaLabel="Adjust price from the current price" onPick={pickPrice} />
          ) : null}
          <input
            className="select num"
            type="number"
            min="0.01"
            step="0.01"
            placeholder="0.00"
            aria-label="Limit price"
            value={limitPrice}
            onChange={(event) => setLimitPrice(event.target.value)}
          />
        </div>
      </div>
      <p className="note note-sm">
        {actors
          .map((actor) =>
            isSell ? `${actor.label}: ${formatInt(actor.owned)} owned` : `${actor.label}: ${formatInt(affordable(actor.balance))} affordable`,
          )
          .join(' · ')}
      </p>
      {error ? (
        <p className="command-error" role="alert">
          {error}
        </p>
      ) : null}
      {confirmation ? (
        <p className="note note-sm" role="status">
          {confirmation}
        </p>
      ) : null}
      <div className="order-actions">
        {actors.map((actor) => (
          <button
            key={actor.key}
            type={actor.key === 'player' ? 'submit' : 'button'}
            className="btn btn-primary"
            disabled={disabledFor(actor)}
            onClick={actor.key === 'player' ? undefined : () => submitFor(actor)}
          >
            {submittingActor === actor.key ? 'Placing…' : hasFund ? `Place ${actor.label} order` : `Place ${side.toLowerCase()} order`}
          </button>
        ))}
      </div>
    </form>
  )
}
