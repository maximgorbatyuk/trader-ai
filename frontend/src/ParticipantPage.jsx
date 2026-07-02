import { useCallback, useEffect, useRef, useState } from 'react'
import { Link, useParams } from 'react-router-dom'
import './App.css'
import { api } from './api'
import { formatInt, formatMoney, formatSigned, toneOf } from './format'
import { Panel } from './Panel'

const POLL_INTERVAL_MS = 2500
const TEMPERAMENTS = ['Aggressive', 'Balanced', 'Conservative']
const RISK_PROFILES = ['High', 'Medium', 'Low']
const TYPE_LABEL = { Individual: 'Individual', Company: 'Company', AIAgent: 'AI agent', CollectiveFund: 'Collective fund', Player: 'Player' }
const FUND_STATUS_LABEL = { Active: 'Active', GoingToBeClosed: 'Closing', Closed: 'Closed' }

function fundStatusClass(status) {
  if (status === 'Active') return 'tag tag-collective'
  if (status === 'Closed') return 'tag tag-bankrupt'
  return 'tag'
}

function ParticipantPage() {
  const { id } = useParams()
  const participantId = Number(id)

  const [ready, setReady] = useState(false)
  const [loadError, setLoadError] = useState(null)
  const [detail, setDetail] = useState(null)
  const [holdings, setHoldings] = useState([])
  const [orders, setOrders] = useState([])
  const [trades, setTrades] = useState([])
  const [cashMoves, setCashMoves] = useState([])
  const [companies, setCompanies] = useState([])

  // The profile selects are editable, so polling must not overwrite an unsaved edit.
  const [form, setForm] = useState({ temperament: '', riskProfile: '' })
  const [dirty, setDirty] = useState(false)
  const [saving, setSaving] = useState(false)
  const [saveError, setSaveError] = useState(null)
  const dirtyRef = useRef(false)
  useEffect(() => {
    dirtyRef.current = dirty
  }, [dirty])

  const loadAll = useCallback(async () => {
    try {
      const [detailData, holdingsData, orderData, tradeData, cashData, companyData] = await Promise.all([
        api.getParticipant(participantId),
        api.getHoldings(participantId),
        api.getParticipantOrders(participantId),
        api.getParticipantShareTransactions(participantId),
        api.getParticipantMoneyTransactions(participantId),
        api.getCompanies(),
      ])

      setDetail(detailData)
      setHoldings(holdingsData)
      setOrders(orderData)
      setTrades(tradeData)
      setCashMoves(cashData)
      setCompanies(companyData)
      setLoadError(null)

      if (!dirtyRef.current && detailData) {
        setForm({ temperament: detailData.temperament, riskProfile: detailData.riskProfile })
      }
    } catch (error) {
      setLoadError(error.message)
    } finally {
      setReady(true)
    }
  }, [participantId])

  useEffect(() => {
    const initialId = setTimeout(loadAll, 0)
    const intervalId = setInterval(loadAll, POLL_INTERVAL_MS)
    return () => {
      clearTimeout(initialId)
      clearInterval(intervalId)
    }
  }, [loadAll])

  async function handleSave() {
    setSaving(true)
    setSaveError(null)
    try {
      const updated = await api.updateParticipantProfile(participantId, {
        temperament: form.temperament,
        riskProfile: form.riskProfile,
      })
      setDetail(updated)
      setForm({ temperament: updated.temperament, riskProfile: updated.riskProfile })
      setDirty(false)
    } catch (error) {
      setSaveError(error.message)
    } finally {
      setSaving(false)
    }
  }

  const companyNameById = new Map(companies.map((company) => [company.id, company.name]))
  const companyName = (companyId) => companyNameById.get(companyId) ?? `#${companyId}`

  const marketValue = holdings.reduce((sum, holding) => sum + holding.marketValue, 0)
  const costBasis = holdings.reduce((sum, holding) => sum + holding.costBasis, 0)
  const holdingsPnl = marketValue - costBasis

  return (
    <div className="app">
      <header className="topbar">
        <Link className="brand" to="/" aria-label="Back to the Trader AI dashboard">
          <span className="brand-mark" aria-hidden="true">
            TA
          </span>
          <span className="brand-name">Trader&nbsp;AI</span>
          <span className="brand-tag" aria-hidden="true">
            Participant
          </span>
        </Link>
        <Link className="btn" to="/">
          ← Dashboard
        </Link>
      </header>

      <main className="main participant-page">
        {!ready ? (
          <section className="placeholder" aria-busy="true">
            <span className="spinner" aria-hidden="true" />
            <p>Loading participant…</p>
          </section>
        ) : detail === null ? (
          <div className="banner" role="alert">
            <strong>Couldn&apos;t load this participant.</strong>
            <span>{loadError ?? 'Try again from the dashboard.'}</span>
          </div>
        ) : (
          <>
            {loadError ? (
              <div className="banner" role="alert">
                <strong>Showing last known state.</strong>
                <span>{loadError}</span>
              </div>
            ) : null}

            <section className="command" aria-label="Participant identity">
              <div className="command-id">
                <span className="command-label">{TYPE_LABEL[detail.type] ?? detail.type}</span>
                <h1 className="command-name">{detail.name}</h1>
                {detail.collectiveFundStatus ? (
                  <span className={fundStatusClass(detail.collectiveFundStatus)}>
                    {FUND_STATUS_LABEL[detail.collectiveFundStatus] ?? detail.collectiveFundStatus}
                  </span>
                ) : null}
                {detail.memberOfCollectiveFundId ? (
                  <p className="command-member">
                    Member of{' '}
                    <Link className="cell-link" to={`/participants/${detail.memberOfCollectiveFundId}`}>
                      {detail.memberOfCollectiveFundName ?? 'a collective fund'}
                    </Link>
                  </p>
                ) : null}
              </div>
              <dl className="statbar">
                <div className="stat">
                  <dt>Available</dt>
                  <dd className="num">{formatMoney(detail.availableBalance)}</dd>
                </div>
                <div className="stat">
                  <dt>Shares owned</dt>
                  <dd className="num">{formatInt(detail.sharesOwned)}</dd>
                </div>
                <div className="stat">
                  <dt>Holdings value</dt>
                  <dd className="num">{formatMoney(marketValue)}</dd>
                </div>
                <div className="stat">
                  <dt>Unrealized P/L</dt>
                  <dd className={`num tone-${toneOf(holdingsPnl)}`}>{formatSigned(holdingsPnl)}</dd>
                </div>
              </dl>
            </section>

            {detail.collectiveFundStatus ? (
              <MembersPanel members={detail.collectiveFundMembers ?? []} />
            ) : null}

            <div className="grid-detail">
              <ProfilePanel
                form={form}
                dirty={dirty}
                saving={saving}
                saveError={saveError}
                onChange={(field, value) => {
                  setForm((current) => ({ ...current, [field]: value }))
                  setDirty(true)
                }}
                onSave={handleSave}
              />
              <BankPanel detail={detail} marketValue={marketValue} costBasis={costBasis} holdingsPnl={holdingsPnl} />
            </div>

            <HoldingsPanel holdings={holdings} />

            <div className="grid-detail">
              <OrdersPanel orders={orders} companyName={companyName} />
              <CashPanel moves={cashMoves} />
            </div>

            <TradesPanel trades={trades} participantId={participantId} companyName={companyName} />
          </>
        )}
      </main>
    </div>
  )
}

function ProfilePanel({ form, dirty, saving, saveError, onChange, onSave }) {
  return (
    <Panel title="Temperament & risk" count="Editable" className="panel-profile">
      <div className="profile-form">
        <label className="field">
          <span>Temperament</span>
          <select
            className="select"
            value={form.temperament}
            onChange={(event) => onChange('temperament', event.target.value)}
          >
            {TEMPERAMENTS.map((value) => (
              <option key={value} value={value}>
                {value}
              </option>
            ))}
          </select>
        </label>
        <label className="field">
          <span>Risk profile</span>
          <select
            className="select"
            value={form.riskProfile}
            onChange={(event) => onChange('riskProfile', event.target.value)}
          >
            {RISK_PROFILES.map((value) => (
              <option key={value} value={value}>
                {value}
              </option>
            ))}
          </select>
        </label>
        <button className="btn btn-primary btn-block" type="button" disabled={!dirty || saving} onClick={onSave}>
          {saving ? 'Saving…' : dirty ? 'Save changes' : 'Saved'}
        </button>
        {saveError ? (
          <p className="command-error" role="alert">
            {saveError}
          </p>
        ) : null}
      </div>
    </Panel>
  )
}

function BankPanel({ detail, marketValue, costBasis, holdingsPnl }) {
  const rows = [
    { label: 'Initial balance', value: formatMoney(detail.initialBalance) },
    { label: 'Current balance', value: formatMoney(detail.currentBalance) },
    { label: 'Reserved', value: formatMoney(detail.reservedBalance) },
    { label: 'Available', value: formatMoney(detail.availableBalance) },
    { label: 'Holdings cost', value: formatMoney(costBasis) },
    { label: 'Holdings value', value: formatMoney(marketValue) },
  ]

  return (
    <Panel title="Bank account" count="Statement" className="panel-bank">
      <dl className="kv">
        {rows.map((row) => (
          <div className="kv-row" key={row.label}>
            <dt>{row.label}</dt>
            <dd className="num">{row.value}</dd>
          </div>
        ))}
        <div className="kv-row kv-total">
          <dt>Unrealized P/L</dt>
          <dd className={`num tone-${toneOf(holdingsPnl)}`}>{formatSigned(holdingsPnl)}</dd>
        </div>
      </dl>
    </Panel>
  )
}

function MembersPanel({ members }) {
  return (
    <Panel title="Fund members" count={`${members.length}`} className="panel-holdings">
      {members.length === 0 ? (
        <p className="note">No members have joined yet.</p>
      ) : (
        <div className="tbl-scroll">
          <table className="tbl">
            <thead>
              <tr>
                <th scope="col">Member</th>
                <th scope="col">Type</th>
                <th scope="col" className="ta-r">
                  Joined
                </th>
                <th scope="col" className="ta-r">
                  Deposit
                </th>
                <th scope="col" className="ta-r">
                  Payouts
                </th>
              </tr>
            </thead>
            <tbody>
              {members.map((member) => (
                <tr key={member.participantId}>
                  <th scope="row" className="cell-ellipsis">
                    <Link className="cell-link" to={`/participants/${member.participantId}`}>
                      {member.name}
                    </Link>
                  </th>
                  <td>
                    <span className="cell-trader">
                      <span className="tag">{TYPE_LABEL[member.type] ?? member.type}</span>
                      {member.isLeaving ? <span className="tag tag-bankrupt">Leaving</span> : null}
                    </span>
                  </td>
                  <td className="num ta-r">cycle {formatInt(member.joinedInCycleNumber)}</td>
                  <td className="num ta-r">{formatMoney(member.deposit)}</td>
                  <td className="num ta-r">{formatMoney(member.payouts)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </Panel>
  )
}

function HoldingsPanel({ holdings }) {
  const totalShares = holdings.reduce((sum, holding) => sum + holding.shares, 0)

  return (
    <Panel title="Shares by company" count={`${formatInt(totalShares)} shares`} className="panel-holdings">
      {holdings.length === 0 ? (
        <p className="note">This participant holds no shares.</p>
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
    </Panel>
  )
}

function OrdersPanel({ orders, companyName }) {
  return (
    <Panel title="Recent orders" count={`last ${orders.length}`} className="panel-orders-list">
      {orders.length === 0 ? (
        <p className="note">No orders placed yet.</p>
      ) : (
        <div className="tbl-scroll">
          <table className="tbl">
            <thead>
              <tr>
                <th scope="col">Side</th>
                <th scope="col">Company</th>
                <th scope="col" className="ta-r">
                  Qty
                </th>
                <th scope="col" className="ta-r">
                  Limit
                </th>
                <th scope="col">Status</th>
              </tr>
            </thead>
            <tbody>
              {orders.map((order) => (
                <tr key={order.id}>
                  <td className={`tone-${order.type === 'Buy' ? 'up' : 'down'}`}>{order.type}</td>
                  <th scope="row" className="cell-ellipsis">
                    {companyName(order.companyId)}
                  </th>
                  <td className="num ta-r">
                    {order.filledQuantity}
                    <span className="muted-sub">/{order.quantity}</span>
                  </td>
                  <td className="num ta-r">{formatMoney(order.limitPrice)}</td>
                  <td>{order.status}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </Panel>
  )
}

function TradesPanel({ trades, participantId, companyName }) {
  return (
    <Panel title="Recent trades" count={`last ${trades.length}`} className="panel-trades">
      {trades.length === 0 ? (
        <p className="note">No settled trades yet.</p>
      ) : (
        <div className="tbl-scroll">
          <table className="tbl">
            <thead>
              <tr>
                <th scope="col">Side</th>
                <th scope="col">Company</th>
                <th scope="col" className="ta-r">
                  Qty
                </th>
                <th scope="col" className="ta-r">
                  Price
                </th>
                <th scope="col" className="ta-r">
                  Total
                </th>
              </tr>
            </thead>
            <tbody>
              {trades.map((trade) => {
                const bought = trade.buyerId === participantId
                return (
                  <tr key={trade.id}>
                    <td className={`tone-${bought ? 'up' : 'down'}`}>{bought ? 'Bought' : 'Sold'}</td>
                    <th scope="row" className="cell-ellipsis">
                      {companyName(trade.companyId)}
                    </th>
                    <td className="num ta-r">{formatInt(trade.quantity)}</td>
                    <td className="num ta-r">{formatMoney(trade.price)}</td>
                    <td className="num ta-r">{formatMoney(trade.totalCost)}</td>
                  </tr>
                )
              })}
            </tbody>
          </table>
        </div>
      )}
    </Panel>
  )
}

const CASH_TONE = { Credit: 'up', Debit: 'down', Reserve: 'flat', Release: 'flat' }

function CashPanel({ moves }) {
  return (
    <Panel title="Cash movements" count={`last ${moves.length}`} className="panel-cash">
      {moves.length === 0 ? (
        <p className="note">No cash movements yet.</p>
      ) : (
        <div className="tbl-scroll">
          <table className="tbl">
            <thead>
              <tr>
                <th scope="col">Type</th>
                <th scope="col" className="ta-r">
                  Amount
                </th>
                <th scope="col" className="ta-r">
                  Cycle
                </th>
              </tr>
            </thead>
            <tbody>
              {moves.map((move) => (
                <tr key={move.id}>
                  <td className={`tone-${CASH_TONE[move.type] ?? 'flat'}`}>{move.type}</td>
                  <td className="num ta-r">{formatMoney(move.amount)}</td>
                  <td className="num ta-r">#{move.createdInCycleId}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </Panel>
  )
}

export default ParticipantPage
