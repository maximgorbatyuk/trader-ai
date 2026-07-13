import { useState } from 'react'
import { api } from './api'
import { formatInt, formatMoney } from './format'
import { PercentButtons } from './PercentButtons'
import { affordability } from './marginModel'
import { luldPresentation } from './marketAccounting'
import { classifyOrderPrice, orderPriceBounds, orderPricePresets } from './orderPriceRange'

const QUANTITY_PRESETS = [
  { label: '10%', value: 0.1 },
  { label: '25%', value: 0.25 },
  { label: '50%', value: 0.5 },
  { label: '75%', value: 0.75 },
  { label: '100%', value: 1 },
]

const ACTOR_ORDER_LABELS = { player: 'Place order as player', fund: 'Place order as managed fund' }

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
  const luld = luldPresentation(company.luldState)
  const luldDisabled = luld.orderEntryDisabled
  const luldReason = luldDisabled ? `${luld.indicator} Order entry disabled during ${luld.label}.` : null

  // Where the typed limit rests against the server-provided band and allowed range. A waiting price still
  // submits (it rests outside the band until the band moves); a price beyond the range or missing bounds blocks.
  const bounds = orderPriceBounds(company)
  const pricePresets = orderPricePresets(company)
  const pricePlacement = classifyOrderPrice(limitPrice, bounds)
  const priceBlocked = pricePlacement === 'outside' || pricePlacement === 'unavailable'
  const priceNote =
    pricePlacement === 'unavailable'
      ? 'Order price bounds are unavailable right now, so orders cannot be placed.'
      : pricePlacement === 'outside'
        ? `Limit price must be between ${formatMoney(bounds.allowedMin)} and ${formatMoney(bounds.allowedMax)}.`
        : pricePlacement === 'waiting'
          ? 'This order will wait outside the executable band until the band moves.'
          : null

  const actors = [
    { key: 'player', label: 'Player', id: player.id, balance: player.availableBalance ?? 0, buyingPower: player.margin?.buyingPower ?? player.availableBalance ?? 0, owned: playerMaxQuantity ?? 0 },
  ]
  if (fund) {
    actors.push({ key: 'fund', label: 'Managed fund', id: fund.id, balance: fund.availableBalance ?? 0, buyingPower: fund.margin?.buyingPower ?? fund.availableBalance ?? 0, owned: fundMaxQuantity ?? 0 })
  }
  const hasFund = actors.length > 1

  function affordable(balance) {
    const price = Number(limitPrice)
    return price > 0 ? Math.floor(balance / price) : 0
  }

  // Presets size against the player: sells at the player's holding, buys at what the player's cash covers.
  function pickQuantity(fraction) {
    const max = isSell ? actors[0].owned : affordable(actors[0].buyingPower)
    setQuantity(String(Math.ceil(max * fraction)))
  }

  // Price presets snap the limit onto a bound: the allowed-range edges, the executable-band edges, or market.
  function pickPrice(value) {
    setLimitPrice(String(value))
  }

  function disabledFor(actor) {
    const qty = Number(quantity)
    const price = Number(limitPrice)
    if (luldDisabled || submittingActor != null || !(qty > 0) || !(price > 0)) return true
    if (priceBlocked) return true
    if (!isSell && qty * price > actor.buyingPower) return true
    if (isSell && qty > actor.owned) return true
    return false
  }

  function eligibilityReason(actor) {
    const qty = Number(quantity)
    const price = Number(limitPrice)
    if (!(qty > 0) || !(price > 0)) return null
    if (isSell && qty > actor.owned) return `${actor.label}: insufficient shares.`
    if (!isSell && qty * price > actor.buyingPower) return `${actor.label}: insufficient margin buying power.`
    return null
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

  // The order's gross value (buy cost or sell proceeds); shown on the submit label once both inputs are set.
  const orderQuantity = Number(quantity)
  const orderPrice = Number(limitPrice)
  const orderTotal = orderQuantity > 0 && orderPrice > 0 ? orderQuantity * orderPrice : null

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
          {pricePresets.length > 0 ? (
            <PercentButtons options={pricePresets} ariaLabel="Set price from the executable band or allowed range" onPick={pickPrice} />
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
            isSell
              ? `${actor.label}: ${formatInt(actor.owned)} owned`
              : (() => {
                  const capacity = affordability(actor.balance, actor.buyingPower, Number(limitPrice))
                  return `${actor.label}: ${formatInt(capacity.cashShares)} cash · ${formatInt(capacity.marginShares)} with margin`
                })(),
          )
          .join(' · ')}
      </p>
      {error ? (
        <p className="command-error" role="alert">
          {error}
        </p>
      ) : null}
      {luldReason ? (
        <p className="note note-sm" role="status">
          {luldReason}
        </p>
      ) : null}
      {priceNote ? (
        <p className="note note-sm" role="status">
          {priceNote}
        </p>
      ) : null}
      {actors.map((actor) => {
        const reason = eligibilityReason(actor)
        return reason ? (
          <p className="note note-sm" role="status" key={actor.key}>
            {reason}
          </p>
        ) : null
      })}
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
            {submittingActor === actor.key
              ? 'Placing…'
              : orderTotal != null
                ? `${ACTOR_ORDER_LABELS[actor.key]} · ${formatMoney(orderTotal)}`
                : ACTOR_ORDER_LABELS[actor.key]}
          </button>
        ))}
      </div>
    </form>
  )
}
