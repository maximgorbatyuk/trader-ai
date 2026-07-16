import { useCallback, useEffect, useRef, useState } from 'react'
import { Link } from 'react-router-dom'
import './App.css'
import { api } from './api'
import { formatCompactMoney, formatInt, formatMoney, formatSigned, formatSignedInt, toneOf } from './format'
import { Panel } from './Panel'
import { LineChart } from './LineChart'
import { CASH_LABEL, CASH_TONE } from './cashMovements'
import { MoneyTransactionModal } from './MoneyTransactionModal'
import { IndustryHoldingsTable } from './IndustryHoldingsTable'
import { groupHoldingsByIndustry } from './industryHoldings'
import { TradeModal } from './TradeModal'
import { InvestmentsTable } from './InvestmentsTable'
import { AiTraderAutomationPanel } from './AiTraderAutomationPanel'
import { AiTraderCallsPanel } from './AiTraderCallsPanel'
import { useClientTable } from './useClientTable'
import { Pager } from './TableControls'
import { SettlementsTable } from './SettlementsTable'
import { cashSettlement, quantitySettlement } from './marketAccounting'
import { maintenanceStanding } from './marginModel'
import { FavoriteCompaniesTable } from './FavoriteCompaniesTable'
import { favoriteCompanies } from './favoriteCompanies'
import { CompanyModal } from './CompanyModal'

const POLL_INTERVAL_MS = 2500
const WORTH_HISTORY_POINTS = 64
const CASH_MOVEMENT_PAGE_SIZE = 10
const TEMPERAMENTS = ['Aggressive', 'Balanced', 'Conservative']
const RISK_PROFILES = ['High', 'Medium', 'Low']
const TYPE_LABEL = { Individual: 'Individual', Company: 'Company', AIAgent: 'AI agent', CollectiveFund: 'Collective fund', Player: 'Player' }
const FUND_STATUS_LABEL = { Active: 'Active', GoingToBeClosed: 'Closing', Closed: 'Closed' }

function fundStatusClass(status) {
  if (status === 'Active') return 'tag tag-collective'
  if (status === 'Closed') return 'tag tag-bankrupt'
  return 'tag'
}

// The trader detail block: identity, a total-worth chart, editable profile, bank statement, holdings, orders,
// cash movements, and trades. Owns its own polling keyed on participantId so it can sit under the Traders
// table and swap as the selected trader changes.
export function ParticipantDetail({ participantId, showFavoriteCompanies = false }) {
  const [ready, setReady] = useState(false)
  const [loadError, setLoadError] = useState(null)
  const [detail, setDetail] = useState(null)
  const [holdings, setHoldings] = useState([])
  const [orders, setOrders] = useState([])
  const [trades, setTrades] = useState([])
  const [investments, setInvestments] = useState([])
  const [companies, setCompanies] = useState([])
  const [worthHistory, setWorthHistory] = useState([])
  const [loans, setLoans] = useState([])
  const [settlements, setSettlements] = useState([])
  const [loanStatus, setLoanStatus] = useState('active')
  const [modalCompanyId, setModalCompanyId] = useState(null)

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
      const [detailData, holdingsData, orderData, tradeData, investmentData, companyData, worthData, loanData, settlementData] = await Promise.all([
        api.getParticipant(participantId),
        api.getHoldings(participantId),
        api.getParticipantOrders(participantId, 100),
        api.getParticipantShareTransactions(participantId),
        api.getParticipantInvestments(participantId),
        api.getCompanies(),
        api.getParticipantWorthHistory(participantId),
        api.getParticipantLoans(participantId, { status: loanStatus }),
        api.getParticipantSettlements(participantId),
      ])

      setDetail(detailData)
      setHoldings(holdingsData)
      setOrders(orderData)
      setTrades(tradeData)
      setInvestments(investmentData ?? [])
      setCompanies(companyData)
      setWorthHistory(worthData ?? [])
      setLoans(loanData ?? [])
      setSettlements(settlementData?.items ?? [])
      setLoadError(null)

      if (!dirtyRef.current && detailData) {
        setForm({ temperament: detailData.temperament, riskProfile: detailData.riskProfile })
      }
    } catch (error) {
      setLoadError(error.message)
    } finally {
      setReady(true)
    }
  }, [participantId, loanStatus])

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
  const modalCompany = companies.find((company) => company.id === modalCompanyId) ?? null

  const marketValue = holdings.reduce((sum, holding) => sum + holding.marketValue, 0)
  const costBasis = holdings.reduce((sum, holding) => sum + holding.costBasis, 0)
  const holdingsPnl = marketValue - costBasis

  if (!ready) {
    return (
      <section className="placeholder" aria-busy="true">
        <span className="spinner" aria-hidden="true" />
        <p>Loading trader…</p>
      </section>
    )
  }

  if (detail === null) {
    return (
      <div className="banner" role="alert">
        <strong>Couldn&apos;t load this trader.</strong>
        <span>{loadError ?? 'Pick another trader from the table.'}</span>
      </div>
    )
  }

  return (
    <section className="detail-stack" aria-label={`${detail.name} details`}>
      {loadError ? (
        <div className="banner" role="alert">
          <strong>Showing last known state.</strong>
          <span>{loadError}</span>
        </div>
      ) : null}

      <section className="command" aria-label="Trader identity">
        <div className="command-id">
          <span className="command-label">{TYPE_LABEL[detail.type] ?? detail.type}</span>
          <h2 className="command-name">{detail.name}</h2>
          {detail.collectiveFundStatus ? (
            <span className={fundStatusClass(detail.collectiveFundStatus)}>
              {FUND_STATUS_LABEL[detail.collectiveFundStatus] ?? detail.collectiveFundStatus}
            </span>
          ) : null}
          {detail.memberOfCollectiveFundId ? (
            <p className="command-member">
              Member of{' '}
              <Link className="cell-link" to={`/traders/${detail.memberOfCollectiveFundId}`}>
                {detail.memberOfCollectiveFundName ?? 'a collective fund'}
              </Link>
            </p>
          ) : null}
          {detail.type === 'AIAgent' ? (
            <p className="command-member">
              <span className="tag">{detail.aiProviderLabel ? `AI · ${detail.aiProviderLabel}` : 'AI'}</span>
              {detail.aiModel ? <span className="tag">{detail.aiModel}</span> : null}
              {detail.aiStatus ? <span className="tag">{detail.aiStatus}</span> : null}
            </p>
          ) : null}
        </div>
        <dl className="statbar">
          <div className="stat">
            <dt>Total worth</dt>
            <dd className="num">{formatMoney(detail.totalWorth)}</dd>
          </div>
          <div className="stat">
            <dt>Settled cash</dt>
            <dd className="num">{formatMoney(detail.settledCashBalance)}</dd>
          </div>
          <div className="stat">
            <dt>Shares owned</dt>
            <dd className="num">{formatInt(detail.sharesOwned)}</dd>
          </div>
          <div className="stat">
            <dt>Holdings value</dt>
            <dd className="num">{formatMoney(marketValue)}</dd>
          </div>
          {detail.loanLiability > 0 ? (
            <div className="stat">
              <dt>Explicit term-loan debt</dt>
              <dd className="num tone-down">−{formatMoney(detail.loanLiability)}</dd>
            </div>
          ) : null}
          <div className="stat"><dt>Account equity</dt><dd className="num">{formatMoney(detail.margin?.accountEquity ?? 0)}</dd></div>
          <div className="stat"><dt>Margin debit</dt><dd className="num">{formatMoney(detail.margin?.debitBalance ?? 0)}</dd></div>
          <div className="stat"><dt>Margin interest</dt><dd className="num">{formatMoney(detail.margin?.accruedInterest ?? 0)}</dd></div>
          <div className="stat"><dt>Buying power</dt><dd className="num">{formatMoney(detail.margin?.buyingPower ?? detail.availableBalance)}</dd></div>
          <div className="stat">
            <dt>Unrealized P/L</dt>
            <dd className={`num tone-${toneOf(holdingsPnl)}`}>{formatSigned(holdingsPnl)}</dd>
          </div>
        </dl>
      </section>

      <WorthChartPanel worthHistory={worthHistory} />

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

      {detail.type === 'Individual' || detail.type === 'AIAgent' ? (
        <>
          <AiTraderAutomationPanel key={`automation-${participantId}`} participantId={participantId} detail={detail} onChanged={loadAll} />
          <AiTraderCallsPanel key={`ai-calls-${participantId}`} participantId={participantId} isAiTrader={detail.type === 'AIAgent'} />
        </>
      ) : null}

      <HoldingsPanel holdings={holdings} onSelectCompany={setModalCompanyId} />

      {showFavoriteCompanies ? (
        <Panel
          title="Favorite companies"
          count={`${formatInt(favoriteCompanies(companies).length)}`}
          className="panel-holdings"
        >
          <FavoriteCompaniesTable companies={companies} />
        </Panel>
      ) : null}

      <MarginPanel margin={detail.margin} />

      <Panel title="Pending settlements" count={`${formatInt(settlements.length)}`} className="panel-holdings">
        <SettlementsTable settlements={settlements} onSelectCompany={setModalCompanyId} />
      </Panel>

      <IndustryHoldingsPanel holdings={holdings} companies={companies} />

      <div className="grid-detail">
        <OrdersPanel orders={orders} companyName={companyName} onSelectCompany={setModalCompanyId} />
        <CashPanel key={participantId} participantId={participantId} />
      </div>

      <LoansPanel loans={loans} status={loanStatus} onStatusChange={setLoanStatus} />

      <FundMembershipHistoryPanel participantId={participantId} isFund={detail.type === 'CollectiveFund'} />

      <TradesPanel trades={trades} participantId={participantId} companyName={companyName} onSelectCompany={setModalCompanyId} />

      <Panel title="Investments made" count={`${investments.length}`} className="panel-trades">
        <InvestmentsTable
          investments={investments}
          showInvestor={false}
          emptyLabel="This trader has funded no capital-raise investments yet."
        />
      </Panel>

      {modalCompany ? (
        <CompanyModal
          company={modalCompany}
          onClose={() => setModalCompanyId(null)}
          onFavoriteChanged={(isFavorite) => {
            setCompanies((current) => current.map((company) =>
              company.id === modalCompany.id ? { ...company, isFavorite } : company))
          }}
        />
      ) : null}
    </section>
  )
}

function WorthChartPanel({ worthHistory }) {
  const values = worthHistory.map((point) => point.totalWorth)
  const change = values.length >= 2 ? values.at(-1) - values.at(0) : 0

  return (
    <Panel
      title="Total worth"
      count={`${worthHistory.length} snapshot${worthHistory.length === 1 ? '' : 's'}`}
      className="panel-worth"
    >
      {values.length < 2 ? (
        <p className="note">Not enough history yet. Total worth is recorded once per completed cycle.</p>
      ) : (
        <LineChart values={values.slice(-WORTH_HISTORY_POINTS)} tone={toneOf(change)} formatValue={formatCompactMoney} label="Total worth over time" />
      )}
    </Panel>
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
  const cash = cashSettlement(detail.currentBalance, detail.settledCashBalance)
  const rows = [
    { label: 'Initial balance', value: formatMoney(detail.initialBalance) },
    { label: 'Total cash', value: formatMoney(cash.total) },
    { label: 'Settled cash', value: formatMoney(cash.settled) },
    { label: 'Pending cash', value: formatMoney(cash.pending) },
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

function MarginPanel({ margin }) {
  const standing = maintenanceStanding(margin?.maintenanceExcess ?? 0)
  const callOpen = margin?.callStatus === 'Open'
  return (
    <Panel title="Margin account" count={callOpen ? 'Call open' : 'Clear'} className="panel-bank">
      <dl className="kv">
        <div className="kv-row"><dt>Account equity</dt><dd className="num">{formatMoney(margin?.accountEquity ?? 0)}</dd></div>
        <div className="kv-row"><dt>Buying power</dt><dd className="num">{formatMoney(margin?.buyingPower ?? 0)}</dd></div>
        <div className="kv-row"><dt>Initial requirement</dt><dd className="num">{formatMoney(margin?.initialRequirement ?? 0)}</dd></div>
        <div className="kv-row"><dt>Maintenance requirement</dt><dd className="num">{formatMoney(margin?.maintenanceRequirement ?? 0)}</dd></div>
        <div className="kv-row"><dt>Maintenance excess</dt><dd className="num">{formatMoney(standing.excess)}</dd></div>
        <div className="kv-row"><dt>Deficiency</dt><dd className="num tone-down">{formatMoney(standing.deficiency)}</dd></div>
        <div className="kv-row kv-total">
          <dt>Call status</dt>
          <dd><span className={callOpen ? 'tag tag-flag' : 'tag'}>{callOpen ? '! Open' : '✓ Clear'}</span></dd>
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
        <div className="tbl-wrap">
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
                <th
                  scope="col"
                  className="ta-r"
                  title="Trading days until the member becomes eligible to leave (negative), then trading days past that point (positive). Founders never switch."
                >
                  Leave in
                </th>
              </tr>
            </thead>
            <tbody>
              {members.map((member) => (
                <tr key={member.participantId}>
                  <th scope="row" className="cell-ellipsis">
                    <Link className="cell-link" to={`/traders/${member.participantId}`}>
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
                  <td className="num ta-r">
                    <MemberLeaveCountdown member={member} />
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </Panel>
  )
}

// A member's standing relative to leave eligibility uses trading days so market pauses and intraday cycles do
// not shorten the lock. Founders keep their label because they never switch away.
function MemberLeaveCountdown({ member }) {
  if (member.isFounder) {
    return <span className="muted-sub">Founder</span>
  }
  if (member.leaveCountdownTradingDays >= 0) {
    return <span className="tag tag-flag">{formatSignedInt(member.leaveCountdownTradingDays)}</span>
  }
  return formatSignedInt(member.leaveCountdownTradingDays)
}

// A company name that opens the shared company modal. Click and key events are stopped so it works inside a
// clickable row (Recent trades) without also triggering that row's own dialog.
function CompanyNameCell({ companyId, companyName, onSelectCompany }) {
  if (!onSelectCompany) {
    return <span className="cell-ellipsis">{companyName}</span>
  }
  return (
    <button
      type="button"
      className="cell-name-btn cell-ellipsis"
      onClick={(event) => {
        event.stopPropagation()
        onSelectCompany(companyId)
      }}
      onKeyDown={(event) => {
        if (event.key === 'Enter' || event.key === ' ') event.stopPropagation()
      }}
      title={`Open ${companyName} details`}
    >
      {companyName}
    </button>
  )
}

function HoldingsPanel({ holdings, onSelectCompany }) {
  const totalShares = holdings.reduce((sum, holding) => sum + holding.shares, 0)

  return (
    <Panel title="Shares by company" count={`${formatInt(totalShares)} shares`} className="panel-holdings">
      {holdings.length === 0 ? (
        <p className="note">This trader holds no shares.</p>
      ) : (
        <div className="tbl-wrap">
          <table className="tbl">
            <thead>
              <tr>
                <th scope="col">Company</th>
                <th scope="col" className="ta-r">
                  Quantity
                </th>
                <th scope="col" className="ta-r">
                  Settled
                </th>
                <th scope="col" className="ta-r">
                  Pending
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
                const quantity = quantitySettlement(holding.shares, holding.settledShares)
                return (
                  <tr key={holding.companyId}>
                    <th scope="row">
                      <CompanyNameCell
                        companyId={holding.companyId}
                        companyName={holding.companyName}
                        onSelectCompany={onSelectCompany}
                      />
                    </th>
                    <td className="num ta-r">{formatInt(quantity.economic)}</td>
                    <td className="num ta-r">{formatInt(quantity.settled)}</td>
                    <td className="num ta-r">{formatSignedInt(quantity.pending)}</td>
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

function IndustryHoldingsPanel({ holdings, companies }) {
  const industryCount = groupHoldingsByIndustry(holdings, companies).length

  return (
    <Panel
      title="Portfolio by industry"
      count={`${formatInt(industryCount)} ${industryCount === 1 ? 'industry' : 'industries'}`}
      className="panel-holdings"
    >
      <IndustryHoldingsTable holdings={holdings} companies={companies} emptyNote="This trader holds no shares." />
    </Panel>
  )
}

function OrdersPanel({ orders, companyName, onSelectCompany }) {
  // Latest first: order ids increase over time, so a descending id sort keeps the newest orders on page one.
  const { pageRows, page, pageCount, setPage } = useClientTable(orders, {
    pageSize: 10,
    initialSortKey: 'id',
    initialSortDir: 'desc',
  })

  return (
    <Panel title="Recent orders" count={`${orders.length} total`} className="panel-orders-list">
      {orders.length === 0 ? (
        <p className="note">No orders placed yet.</p>
      ) : (
        <>
          <div className="tbl-wrap">
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
                {pageRows.map((order) => (
                  <tr key={order.id}>
                    <td className={`tone-${order.type === 'Buy' ? 'up' : 'down'}`}>{order.type}</td>
                    <th scope="row">
                      <CompanyNameCell
                        companyId={order.companyId}
                        companyName={companyName(order.companyId)}
                        onSelectCompany={onSelectCompany}
                      />
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
          <Pager page={page} pageCount={pageCount} onPage={setPage} />
        </>
      )}
    </Panel>
  )
}

function TradesPanel({ trades, participantId, companyName, onSelectCompany }) {
  const [selectedTrade, setSelectedTrade] = useState(null)

  return (
    <Panel title="Recent trades" count={`last ${trades.length}`} className="panel-trades">
      {trades.length === 0 ? (
        <p className="note">No settled trades yet.</p>
      ) : (
        <div className="tbl-wrap">
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
                  <tr
                    key={trade.id}
                    className="tbl-row-click"
                    role="button"
                    tabIndex={0}
                    aria-label={`Open details for ${bought ? 'buying' : 'selling'} ${formatInt(trade.quantity)} ${companyName(trade.companyId)} shares at ${formatMoney(trade.price)}`}
                    onClick={() => setSelectedTrade(trade)}
                    onKeyDown={(event) => {
                      if (event.key === 'Enter' || event.key === ' ') {
                        event.preventDefault()
                        setSelectedTrade(trade)
                      }
                    }}
                  >
                    <td className={`tone-${bought ? 'up' : 'down'}`}>{bought ? 'Bought' : 'Sold'}</td>
                    <th scope="row">
                      <CompanyNameCell
                        companyId={trade.companyId}
                        companyName={companyName(trade.companyId)}
                        onSelectCompany={onSelectCompany}
                      />
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
      {selectedTrade ? (
        <TradeModal
          trade={selectedTrade}
          companyName={companyName(selectedTrade.companyId)}
          participantId={participantId}
          onClose={() => setSelectedTrade(null)}
        />
      ) : null}
    </Panel>
  )
}

function LoansPanel({ loans, status, onStatusChange }) {
  return (
    <Panel title="Explicit term loans" count={status === 'all' ? 'All' : 'Active'} className="panel-holdings">
      <div className="roster-toolbar">
        <select
          className="select select-sm"
          aria-label="Filter loans by status"
          value={status}
          onChange={(event) => onStatusChange(event.target.value)}
        >
          <option value="active">Active</option>
          <option value="all">All</option>
        </select>
      </div>
      {loans.length === 0 ? (
        <p className="note">{status === 'all' ? 'No explicit term loans.' : 'No active explicit term loans.'}</p>
      ) : (
        <div className="tbl-wrap">
          <table className="tbl">
            <thead>
              <tr>
                <th scope="col">Bank</th>
                <th scope="col" className="ta-r">
                  Taken
                </th>
                <th scope="col" className="ta-r">
                  Interest/cyc
                </th>
                <th scope="col" className="ta-r">
                  Remaining
                </th>
                <th scope="col" className="ta-r">
                  Principal due
                </th>
                <th scope="col" className="ta-r">
                  Interest due
                </th>
                <th scope="col" className="ta-r">
                  Fees
                </th>
                <th scope="col" className="ta-r">
                  Total
                </th>
                <th scope="col" className="ta-r">
                  Term left
                </th>
                <th scope="col">Status</th>
              </tr>
            </thead>
            <tbody>
              {loans.map((loan) => (
                <tr key={loan.id}>
                  <th scope="row" className="cell-ellipsis">
                    {loan.bankName}
                  </th>
                  <td className="num ta-r">{formatMoney(loan.principal)}</td>
                  <td className="num ta-r">
                    {formatMoney(loan.interestPerCycleAmount)}
                    <span className="muted-sub"> {(loan.interestRatePerCycle * 100).toFixed(3)}%</span>
                  </td>
                  <td className="num ta-r">{formatMoney(loan.remainingPrincipal)}</td>
                  <td className={`num ta-r${loan.pastDuePrincipal > 0 ? ' tone-attention' : ' muted-sub'}`}>
                    {formatMoney(loan.pastDuePrincipal)}
                  </td>
                  <td className={`num ta-r${loan.pastDueInterest > 0 ? ' tone-attention' : ' muted-sub'}`}>
                    {formatMoney(loan.pastDueInterest)}
                  </td>
                  <td className={`num ta-r${loan.accruedFees > 0 ? ' tone-attention' : ' muted-sub'}`}>
                    {formatMoney(loan.accruedFees)}
                  </td>
                  <td className="num ta-r">{formatMoney(loan.totalLiability)}</td>
                  <td className="num ta-r">{loan.isClosed ? '—' : `${formatInt(loan.remainingTermCycles)} cyc`}</td>
                  <td>
                    {loan.isClosed ? (
                      <span className="tag" title={loan.closeReason ?? undefined}>
                        Closed
                      </span>
                    ) : (
                      <span className="tag tag-flag">Open</span>
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </Panel>
  )
}

const MEMBERSHIP_HISTORY_PAGE_SIZE = 10

// Join/leave history for a fund or a trader, paged newest-first from the shared endpoint. On a fund's page the
// counterparty column names the member; on a trader's page it names the fund. Kept off pages that never touch a
// fund by rendering nothing once the load confirms there is no history and the participant is not itself a fund.
function FundMembershipHistoryPanel({ participantId, isFund }) {
  const [ready, setReady] = useState(false)
  const [data, setData] = useState(null)
  const [page, setPage] = useState(1)

  const loadHistory = useCallback(async () => {
    try {
      setData(await api.getFundMembershipHistory(participantId, page, MEMBERSHIP_HISTORY_PAGE_SIZE))
    } catch {
      // Keep the last page on a failed refresh; the detail header already surfaces the offline state.
    } finally {
      setReady(true)
    }
  }, [participantId, page])

  useEffect(() => {
    const initialId = setTimeout(loadHistory, 0)
    const intervalId = setInterval(loadHistory, POLL_INTERVAL_MS)
    return () => {
      clearTimeout(initialId)
      clearInterval(intervalId)
    }
  }, [loadHistory])

  const total = data?.total ?? 0
  const items = data?.items ?? []
  const pageCount = Math.max(1, Math.ceil(total / MEMBERSHIP_HISTORY_PAGE_SIZE))

  if (!ready || (!isFund && total === 0)) {
    return null
  }

  const counterpartyLabel = isFund ? 'Member' : 'Fund'

  return (
    <Panel title="Fund membership history" count={`${formatInt(total)}`} className="panel-holdings">
      {items.length === 0 ? (
        <p className="note">No members have joined or left yet.</p>
      ) : (
        <>
          <div className="tbl-wrap">
            <table className="tbl">
              <thead>
                <tr>
                  <th scope="col">Event</th>
                  <th scope="col">{counterpartyLabel}</th>
                  <th scope="col" className="ta-r">
                    Amount
                  </th>
                  <th scope="col" className="ta-r">
                    Cycle
                  </th>
                </tr>
              </thead>
              <tbody>
                {items.map((event) => {
                  const joined = event.type === 'Joined'
                  const counterpartyId = isFund ? event.memberParticipantId : event.fundParticipantId
                  const counterpartyName = isFund ? event.memberName : event.fundName
                  return (
                    <tr key={event.id}>
                      <td>
                        <span className={`tag ${joined ? 'tag-collective' : 'tag-bankrupt'}`}>
                          {joined ? 'Joined' : 'Left'}
                        </span>
                      </td>
                      <th scope="row" className="cell-ellipsis">
                        <Link className="cell-link" to={`/traders/${counterpartyId}`}>
                          {counterpartyName}
                        </Link>
                      </th>
                      <td className="num ta-r">{formatMoney(event.amount)}</td>
                      <td className="num ta-r">cycle {formatInt(event.createdInCycleNumber || event.createdInCycleId)}</td>
                    </tr>
                  )
                })}
              </tbody>
            </table>
          </div>
          <Pager page={page} pageCount={pageCount} onPage={setPage} />
        </>
      )}
    </Panel>
  )
}

function CashPanel({ participantId }) {
  const [ready, setReady] = useState(false)
  const [data, setData] = useState(null)
  const [page, setPage] = useState(1)
  const [loadError, setLoadError] = useState(null)
  const [selectedMove, setSelectedMove] = useState(null)
  const requestSequence = useRef(0)

  const loadMoves = useCallback(async () => {
    const requestId = ++requestSequence.current
    try {
      const result = await api.getParticipantMoneyTransactionsPaged(participantId, page, CASH_MOVEMENT_PAGE_SIZE)
      if (requestId !== requestSequence.current) return

      const resultPageCount = Math.max(1, Math.ceil(result.total / (result.pageSize || CASH_MOVEMENT_PAGE_SIZE)))
      if (page > resultPageCount) {
        setPage(resultPageCount)
        return
      }

      setData(result)
      setLoadError(null)
    } catch (error) {
      if (requestId !== requestSequence.current) return
      setLoadError(error.message)
    } finally {
      if (requestId === requestSequence.current) setReady(true)
    }
  }, [participantId, page])

  useEffect(() => {
    const initialId = setTimeout(loadMoves, 0)
    const intervalId = setInterval(loadMoves, POLL_INTERVAL_MS)
    return () => {
      clearTimeout(initialId)
      clearInterval(intervalId)
      requestSequence.current += 1
    }
  }, [loadMoves])

  const moves = data?.items ?? []
  const total = data?.total ?? 0
  const pageCount = Math.max(1, Math.ceil(total / (data?.pageSize || CASH_MOVEMENT_PAGE_SIZE)))
  const displayedPage = data?.page ?? page

  return (
    <Panel title="Cash movements" count={ready ? `${formatInt(total)} total · newest first` : 'loading'} className="panel-cash">
      {loadError && data != null ? <p className="note">Unable to refresh cash movements: {loadError}</p> : null}
      {!ready && data == null ? (
        <p className="note">Loading cash movements…</p>
      ) : loadError && data == null ? (
        <p className="note">Unable to load cash movements: {loadError}</p>
      ) : total === 0 ? (
        <p className="note">No cash movements yet.</p>
      ) : (
        <>
          {moves.length === 0 ? (
            <p className="note">No cash movements on this page.</p>
          ) : (
            <div className="tbl-wrap">
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
                    <tr
                      key={move.id}
                      className="tbl-row-click"
                      role="button"
                      tabIndex={0}
                      aria-label={`Open details for ${CASH_LABEL[move.type] ?? move.type} of ${formatMoney(move.amount)}`}
                      onClick={() => setSelectedMove(move)}
                      onKeyDown={(event) => {
                        if (event.key === 'Enter' || event.key === ' ') {
                          event.preventDefault()
                          setSelectedMove(move)
                        }
                      }}
                    >
                      <td>
                        <span className={`tone-${CASH_TONE[move.type] ?? 'flat'}`}>{CASH_LABEL[move.type] ?? move.type}</span>
                      </td>
                      <td className="num ta-r">{formatMoney(move.amount)}</td>
                      <td className="num ta-r">#{move.createdInCycleId}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
          <Pager page={displayedPage} pageCount={pageCount} onPage={setPage} />
        </>
      )}
      {selectedMove ? (
        <MoneyTransactionModal
          transaction={selectedMove}
          participantId={participantId}
          onClose={() => setSelectedMove(null)}
        />
      ) : null}
    </Panel>
  )
}
