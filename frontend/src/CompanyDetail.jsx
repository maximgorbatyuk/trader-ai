import { useCallback, useEffect, useRef, useState } from 'react'
import { Link } from 'react-router-dom'
import './App.css'
import { api, loadCompanyAuditsForActiveTab } from './api'
import {
  companyRiskTrendGlyph,
  formatCompactMoney,
  formatInt,
  formatMoney,
  formatSigned,
  ratingTrend,
  toneOf,
} from './format'
import { Panel } from './Panel'
import { LineChart } from './LineChart'
import { RatingBadge } from './RatingBadge'
import { NewsImpact } from './NewsImpact'
import { NewsModal } from './NewsModal'
import { OrderForm } from './OrderForm'
import { PercentButtons } from './PercentButtons'
import { InvestmentsTable } from './InvestmentsTable'
import { TradeModal } from './TradeModal'
import { Pager, SortHeader } from './TableControls'
import { useClientTable } from './useClientTable'
import { useFitPageSize } from './useFitPageSize'
import { corporateCashMovementPresentation } from './cashMovements'
import { luldPresentation } from './marketAccounting'
import { FavoriteCompanyToggle } from './FavoriteCompanyToggle'
import { CompanyFinancialsPanel } from './CompanyFinancialsPanel'
import { CompanyFinancialHistoryPanel } from './CompanyFinancialHistoryPanel'
import { CompanyManagementOutlookPanel } from './CompanyManagementOutlookPanel'
import { AuditDetailModal } from './AuditDetailModal'
import { createFinancialHistoryQueryLoader, refreshCompanyDetailRequests } from './companyDetailRefresh'

export { CompanyFinancialHistoryPanel, CompanyFinancialsPanel, CompanyManagementOutlookPanel }

const POLL_INTERVAL_MS = 2500
const PRICE_HISTORY_POINTS = 32
const CORPORATE_CASH_PAGE_SIZE = 10
const FINANCIAL_HISTORY_PAGE_SIZE = 6
const AUDIT_HISTORY_PAGE_SIZE = 6

// The tabbed detail sections, in display order. Each key selects one existing panel below the tab strip.
const DETAIL_TABS = [
  { key: 'capitalization', label: 'Capitalization' },
  { key: 'financials', label: 'Financials' },
  { key: 'financial-history', label: 'Financial history' },
  { key: 'management', label: 'Management outlook' },
  { key: 'cash', label: 'Cash movements' },
  { key: 'shareholders', label: 'Shareholders' },
  { key: 'orders', label: 'Recent orders' },
  { key: 'trades', label: 'Recent trades' },
  { key: 'emissions', label: 'Share emissions' },
  { key: 'audits', label: 'Audits' },
  { key: 'investments', label: 'Investments received' },
  { key: 'news', label: 'Related news' },
]

function formatPct(fraction) {
  if (typeof fraction !== 'number') return '—'
  const sign = fraction > 0 ? '+' : fraction < 0 ? '−' : ''
  return `${sign}${(Math.abs(fraction) * 100).toFixed(2)}%`
}

// An order/trade quantity as a share of the whole issued float, shown unsigned; sub-0.01% quantities collapse
// to a floor label so a tiny order does not read as exactly 0%.
function formatSharePct(quantity, issuedShares) {
  if (!issuedShares || issuedShares <= 0) return null
  const pct = (quantity / issuedShares) * 100
  if (pct > 0 && pct < 0.01) return '<0.01%'
  return `${pct.toFixed(2)}%`
}

// A price relative to a reference (order limit vs current price, trade price vs the market it hit), signed.
function priceVsReference(price, reference) {
  if (typeof price !== 'number' || typeof reference !== 'number' || reference === 0) return null
  return formatPct((price - reference) / reference)
}

// The company detail block: identity with market controls, an always-visible quick-stats bar, and a tabbed
// section holding the price chart, ownership and shareholders, orders and trades, and the related history
// tables. Owns its own polling keyed on companyId so it can sit under the Companies table and swap as the
// selected company changes.
export function CompanyDetail({ companyId }) {
  const [ready, setReady] = useState(false)
  const [loadError, setLoadError] = useState(null)
  const [detail, setDetail] = useState(null)
  const [shareholders, setShareholders] = useState([])
  const [orders, setOrders] = useState([])
  const [trades, setTrades] = useState([])
  const [prices, setPrices] = useState([])
  const [emissions, setEmissions] = useState([])
  const [investments, setInvestments] = useState([])
  const [news, setNews] = useState([])
  const [corporateCashMovements, setCorporateCashMovements] = useState({ items: [], total: 0, page: 1, pageSize: 10 })
  const [corporateCashPage, setCorporateCashPage] = useState(1)
  const [corporateCashPageSize, corporateCashTableRef] = useFitPageSize()
  const [financialHistoryPage, setFinancialHistoryPage] = useState(1)
  const [financialHistoryState, setFinancialHistoryState] = useState({
    data: { items: [], total: 0, page: 1, pageSize: FINANCIAL_HISTORY_PAGE_SIZE },
    status: 'idle',
    error: null,
  })
  const financialHistoryQueryLoader = useRef(null)
  if (financialHistoryQueryLoader.current === null) {
    financialHistoryQueryLoader.current = createFinancialHistoryQueryLoader({
      request: api.getCompanyFinancials,
      onStart() {
        setFinancialHistoryState((current) => ({
          ...current,
          status: current.data.items.length > 0 ? 'refreshing' : 'loading',
          error: null,
        }))
      },
      onSuccess(data, query) {
        setFinancialHistoryState({
          data: data ?? {
            items: [],
            total: 0,
            page: query.page,
            pageSize: query.pageSize,
          },
          status: 'ready',
          error: null,
        })
      },
      onError(error) {
        setFinancialHistoryState((current) => ({
          ...current,
          status: 'error',
          error: error.message ?? String(error),
        }))
      },
    })
  }
  const [auditHistoryPage, setAuditHistoryPage] = useState(1)
  const [auditHistoryState, setAuditHistoryState] = useState({
    data: { items: [], total: 0, page: 1, pageSize: AUDIT_HISTORY_PAGE_SIZE },
    status: 'idle',
    error: null,
  })
  const auditHistoryRequestId = useRef(0)
  const [selectedAuditId, setSelectedAuditId] = useState(null)
  const [selectedNews, setSelectedNews] = useState(null)
  const [activeTab, setActiveTab] = useState('capitalization')
  const [action, setAction] = useState(null)
  const [player, setPlayer] = useState(null)
  const [playerOwned, setPlayerOwned] = useState(0)
  const [fundOwned, setFundOwned] = useState(0)

  const refreshFinancialHistory = useCallback(
    () =>
      financialHistoryQueryLoader.current.refresh({
        companyId,
        page: financialHistoryPage,
        pageSize: FINANCIAL_HISTORY_PAGE_SIZE,
      }),
    [companyId, financialHistoryPage],
  )

  useEffect(() => {
    financialHistoryQueryLoader.current.setActiveQuery({
      companyId,
      page: financialHistoryPage,
      pageSize: FINANCIAL_HISTORY_PAGE_SIZE,
    })
  }, [companyId, financialHistoryPage])

  const refreshAuditHistory = useCallback(() => {
    const auditHistoryRequest = loadCompanyAuditsForActiveTab({
      activeTab: 'audits',
      companyId,
      page: auditHistoryPage,
      pageSize: AUDIT_HISTORY_PAGE_SIZE,
    })
    const auditHistoryRequestNumber = auditHistoryRequest
      ? auditHistoryRequestId.current + 1
      : auditHistoryRequestId.current
    if (auditHistoryRequest) {
      auditHistoryRequestId.current = auditHistoryRequestNumber
      setAuditHistoryState((current) => ({
        ...current,
        status: current.data.items.length > 0 ? 'refreshing' : 'loading',
        error: null,
      }))
    }
    const auditHistoryResult = auditHistoryRequest
      ? Promise.resolve(auditHistoryRequest).then(
          (data) => ({ data, error: null }),
          (error) => ({ data: null, error: error.message ?? String(error) }),
        )
      : null

    if (!auditHistoryResult) return null
    return auditHistoryResult.then((result) => {
      if (auditHistoryRequestNumber !== auditHistoryRequestId.current) return
      if (result.error) {
        setAuditHistoryState((current) => ({
          ...current,
          status: 'error',
          error: result.error,
        }))
      } else {
        setAuditHistoryState({
          data: result.data ?? {
            items: [],
            total: 0,
            page: auditHistoryPage,
            pageSize: AUDIT_HISTORY_PAGE_SIZE,
          },
          status: 'ready',
          error: null,
        })
      }
    })
  }, [auditHistoryPage, companyId])

  const loadBase = useCallback(async () => {
    try {
      const [
        detailData,
        shareholderData,
        orderData,
        tradeData,
        priceData,
        emissionData,
        investmentData,
        newsData,
        corporateCashData,
        playerData,
      ] =
        await Promise.all([
          api.getCompany(companyId),
          api.getCompanyShareholders(companyId),
          api.getCompanyOrders(companyId),
          api.getCompanyShareTransactions(companyId),
          api.getPrices(companyId),
          api.getCompanyEmissions(companyId),
          api.getCompanyInvestments(companyId),
          api.getCompanyNews(companyId),
          api.getCompanyCorporateCashMovements(companyId, corporateCashPage, corporateCashPageSize),
          api.getPlayer(),
        ])

      setDetail(detailData)
      setShareholders(shareholderData)
      setOrders(orderData)
      setTrades(tradeData)
      setPrices(priceData)
      setEmissions(emissionData ?? [])
      setInvestments(investmentData ?? [])
      setNews(newsData ?? [])
      setCorporateCashMovements(corporateCashData ?? { items: [], total: 0, page: 1, pageSize: corporateCashPageSize })

      setPlayer(playerData)
      if (playerData) {
        const holdings = await api.getHoldings(playerData.id)
        const owned = holdings.find((item) => item.companyId === companyId)
        setPlayerOwned(owned ? owned.shares : 0)
        if (playerData.fundParticipantId != null) {
          const fundHoldings = await api.getHoldings(playerData.fundParticipantId)
          const fundHolding = fundHoldings.find((item) => item.companyId === companyId)
          setFundOwned(fundHolding ? fundHolding.shares : 0)
        } else {
          setFundOwned(0)
        }
      } else {
        setPlayerOwned(0)
        setFundOwned(0)
      }
      setLoadError(null)
    } catch (error) {
      setLoadError(error.message)
    } finally {
      setReady(true)
    }
  }, [companyId, corporateCashPage, corporateCashPageSize])

  const activeRefresh = useRef(null)
  useEffect(() => {
    activeRefresh.current = {
      activeTab,
      refreshAuditHistory,
      refreshFinancialHistory,
    }
  }, [activeTab, refreshAuditHistory, refreshFinancialHistory])
  const loadAll = useCallback(
    (includeBase = true) =>
      refreshCompanyDetailRequests({
        activeTab: activeRefresh.current.activeTab,
        includeBase,
        refreshBase: loadBase,
        refreshFinancialHistory: activeRefresh.current.refreshFinancialHistory,
        refreshAuditHistory: activeRefresh.current.refreshAuditHistory,
      }),
    [loadBase],
  )

  useEffect(() => {
    loadAll(false)
  }, [activeTab, auditHistoryPage, companyId, financialHistoryPage, loadAll])

  useEffect(() => {
    const initialId = setTimeout(loadAll, 0)
    const intervalId = setInterval(loadAll, POLL_INTERVAL_MS)
    return () => {
      clearTimeout(initialId)
      clearInterval(intervalId)
    }
  }, [loadAll])

  if (!ready) {
    return (
      <section className="placeholder" aria-busy="true">
        <span className="spinner" aria-hidden="true" />
        <p>Loading company…</p>
      </section>
    )
  }

  if (detail === null) {
    return (
      <div className="banner" role="alert">
        <strong>Couldn&apos;t load this company.</strong>
        <span>{loadError ?? 'Pick another company from the table.'}</span>
      </div>
    )
  }

  const changeTone = toneOf(detail.priceChangePct)
  const riskTrend = ratingTrend(detail.currentRating, detail.previousRating)
  const luld = luldPresentation(detail.luldState)
  const showLuld = detail.luldState && detail.luldState !== 'Normal'

  const canAct = Boolean(player) && !detail.isClosed
  const canSell = playerOwned > 0 || fundOwned > 0
  const fund =
    player && player.fundParticipantId != null
      ? { id: player.fundParticipantId, name: player.fundName, availableBalance: player.fundAvailableBalance, margin: player.fundMargin }
      : null
  const companyForOrder = {
    id: companyId,
    currentPrice: detail.currentPrice,
    luldState: detail.luldState,
    lowerBandPrice: detail.lowerBandPrice,
    upperBandPrice: detail.upperBandPrice,
    minimumOrderPrice: detail.minimumOrderPrice,
    maximumOrderPrice: detail.maximumOrderPrice,
  }

  return (
    <section className="detail-stack" aria-label={`${detail.name} details`}>
      {loadError ? (
        <div className="banner" role="alert">
          <strong>Showing last known state.</strong>
          <span>{loadError}</span>
        </div>
      ) : null}

      {detail.isClosed ? (
        <div className="banner" role="status">
          <strong>Delisted.</strong>
          <span>
            This company was delisted
            {typeof detail.closedInCycleNumber === 'number' ? ` in cycle ${detail.closedInCycleNumber}` : ''}. Its
            orders were cancelled and its shares wiped out.
          </span>
        </div>
      ) : null}

      <section className="command command-compact" aria-label="Company identity">
        <div className="command-id">
          <span className="command-label">Company</span>
          <h2 className="command-name">{detail.name}</h2>
          {player ? (
            <div className="command-buttons">
              <FavoriteCompanyToggle
                companyId={detail.id}
                companyName={detail.name}
                isFavorite={detail.isFavorite}
                onChanged={(isFavorite) => setDetail((current) => ({ ...current, isFavorite }))}
              />
              {canAct ? <CompanyActions canSell={canSell} onSelect={setAction} /> : null}
            </div>
          ) : null}
        </div>
        <dl className="statbar">
          <div className="stat">
            <dt>Industry</dt>
            <dd>{detail.industryName ?? '—'}</dd>
          </div>
          <div className="stat">
            <dt>Price control</dt>
            <dd className={`tone-${luld.tone}`}>
              <span aria-hidden="true">{luld.indicator} </span>{luld.label}
            </dd>
          </div>
          <div className="stat">
            <dt>Risk rating</dt>
            <dd>
              {detail.currentRating ? (
                <span className="rating-stat">
                  <RatingBadge rating={detail.currentRating} />
                  {riskTrend ? (
                    <span className="rating-trend">
                      {companyRiskTrendGlyph(riskTrend)} {riskTrend}
                    </span>
                  ) : null}
                </span>
              ) : (
                '—'
              )}
            </dd>
          </div>
        </dl>
        <CompanyQuickStats detail={detail} changeTone={changeTone} />
        {showLuld ? (
          <p className="command-status" role="status">
            <strong>
              {luld.indicator} {luld.label}.
            </strong>{' '}
            Reference {formatMoney(detail.referencePrice)} · band {formatMoney(detail.lowerBandPrice)}–
            {formatMoney(detail.upperBandPrice)}
            {detail.luldState === 'TradingPause' ? ` · ${formatInt(detail.remainingPauseSeconds)} trading seconds left` : ''}.
            {' '}{luld.executionNote}
          </p>
        ) : null}
      </section>

      <CompanyDetailTabs
        activeTab={activeTab}
        onTab={setActiveTab}
        detail={detail}
        prices={prices}
        corporateCashMovements={corporateCashMovements}
        corporateCashPage={corporateCashPage}
        onCorporateCashPage={setCorporateCashPage}
        corporateCashTableRef={corporateCashTableRef}
        financialHistory={financialHistoryState.data}
        financialHistoryPage={financialHistoryPage}
        onFinancialHistoryPage={setFinancialHistoryPage}
        financialHistoryLoading={
          financialHistoryState.status === 'idle' ||
          financialHistoryState.status === 'loading' ||
          financialHistoryState.status === 'refreshing'
        }
        financialHistoryError={financialHistoryState.error}
        auditHistory={auditHistoryState.data}
        auditHistoryPage={auditHistoryPage}
        onAuditHistoryPage={setAuditHistoryPage}
        auditHistoryLoading={
          auditHistoryState.status === 'idle' ||
          auditHistoryState.status === 'loading' ||
          auditHistoryState.status === 'refreshing'
        }
        auditHistoryError={auditHistoryState.error}
        onSelectAudit={setSelectedAuditId}
        shareholders={shareholders}
        orders={orders}
        trades={trades}
        emissions={emissions}
        investments={investments}
        news={news}
        onSelectNews={setSelectedNews}
      />

      {canAct && action === 'buy' ? (
        <ActionDialog label="Order" title={`Buy ${detail.name}`} onClose={() => setAction(null)}>
          <OrderForm key={`buy-${companyId}`} player={player} fund={fund} company={companyForOrder} side="Buy" onPlaced={loadAll} />
        </ActionDialog>
      ) : null}

      {canAct && action === 'sell' ? (
        <ActionDialog label="Order" title={`Sell ${detail.name}`} onClose={() => setAction(null)}>
          <OrderForm
            key={`sell-${companyId}`}
            player={player}
            fund={fund}
            company={companyForOrder}
            side="Sell"
            playerMaxQuantity={playerOwned}
            fundMaxQuantity={fundOwned}
            onPlaced={loadAll}
          />
        </ActionDialog>
      ) : null}

      {canAct && action === 'invest' ? (
        <ActionDialog label="Capital raise" title={`Invest in ${detail.name}`} onClose={() => setAction(null)}>
          <InvestmentForm
            companyId={companyId}
            currentPrice={detail.currentPrice}
            marketCap={detail.marketCap}
            player={player}
            onPlaced={loadAll}
          />
        </ActionDialog>
      ) : null}

      {selectedNews ? <NewsModal post={selectedNews} onClose={() => setSelectedNews(null)} /> : null}
      {selectedAuditId != null ? (
        <AuditDetailModal
          companyId={companyId}
          auditId={selectedAuditId}
          onClose={() => setSelectedAuditId(null)}
        />
      ) : null}
    </section>
  )
}

// The Actions menu under the favorite toggle: a single button that reveals Buy/Sell/Make investment, each of
// which opens its own dialog. Sell is disabled when neither the player nor the fund owns shares.
function CompanyActions({ canSell, onSelect }) {
  const [open, setOpen] = useState(false)
  const buttonRef = useRef(null)
  const menuRef = useRef(null)

  useEffect(() => {
    if (!open) return undefined
    function onPointerDown(event) {
      if (buttonRef.current?.contains(event.target) || menuRef.current?.contains(event.target)) return
      setOpen(false)
    }
    function onKeyDown(event) {
      if (event.key === 'Escape') {
        setOpen(false)
        buttonRef.current?.focus()
      }
    }
    document.addEventListener('mousedown', onPointerDown)
    document.addEventListener('keydown', onKeyDown)
    return () => {
      document.removeEventListener('mousedown', onPointerDown)
      document.removeEventListener('keydown', onKeyDown)
    }
  }, [open])

  function choose(next) {
    setOpen(false)
    onSelect(next)
  }

  return (
    <div className="command-actions">
      <button
        ref={buttonRef}
        type="button"
        className="btn actions-toggle"
        aria-haspopup="menu"
        aria-expanded={open}
        onClick={() => setOpen((current) => !current)}
      >
        Actions
        <span className="actions-caret" aria-hidden="true">▾</span>
      </button>
      {open ? (
        <div className="actions-menu" role="menu" ref={menuRef} aria-label="Company actions">
          <button type="button" role="menuitem" className="actions-menu-item" onClick={() => choose('buy')}>
            Buy shares
          </button>
          <button
            type="button"
            role="menuitem"
            className="actions-menu-item"
            disabled={!canSell}
            onClick={() => choose('sell')}
          >
            Sell shares
          </button>
          <button type="button" role="menuitem" className="actions-menu-item" onClick={() => choose('invest')}>
            Make investment
          </button>
        </div>
      ) : null}
    </div>
  )
}

// Shared dialog shell for the company actions: backdrop dismissal, Escape to close, a focus trap, and focus
// restored to the trigger on close. Mirrors the news/trade modals so the action dialogs behave the same.
function ActionDialog({ label, title, onClose, children }) {
  const dialogRef = useRef(null)

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
    const focusable = dialogRef.current?.querySelector(
      'input, select, textarea, a[href], button:not([disabled]), [tabindex]:not([tabindex="-1"])',
    )
    focusable?.focus()
    return () => {
      if (previouslyFocused instanceof HTMLElement) previouslyFocused.focus()
    }
  }, [])

  function onBackdropClick(event) {
    if (event.target === event.currentTarget) onClose()
  }

  // Keep Tab focus inside the dialog by wrapping it at the first and last focusable controls.
  function onDialogKeyDown(event) {
    if (event.key !== 'Tab') return
    const focusable = dialogRef.current?.querySelectorAll(
      'input, select, textarea, a[href], button:not([disabled]), [tabindex]:not([tabindex="-1"])',
    )
    if (!focusable || focusable.length === 0) return
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

  const titleId = `action-dialog-title-${label.replace(/\s+/g, '-').toLowerCase()}`

  return (
    <div className="modal-backdrop" onClick={onBackdropClick}>
      <div
        className="modal modal-action"
        role="dialog"
        aria-modal="true"
        aria-labelledby={titleId}
        ref={dialogRef}
        onKeyDown={onDialogKeyDown}
      >
        <header className="modal-head">
          <div className="command-id">
            <span className="command-label">{label}</span>
            <h2 className="command-name" id={titleId}>
              {title}
            </h2>
          </div>
          <button type="button" className="btn" onClick={onClose}>
            Close
          </button>
        </header>
        <div className="modal-body">{children}</div>
      </div>
    </div>
  )
}

// The market snapshot as the identity bar's second row: a full-width numeric statbar so price and market cap
// stay on screen no matter which detail tab is open.
function CompanyQuickStats({ detail, changeTone }) {
  return (
    <dl className="statbar command-metrics" aria-label="Company market stats">
      <div className="stat">
        <dt>Price</dt>
        <dd className="num">{formatMoney(detail.currentPrice)}</dd>
      </div>
      <div className="stat">
        <dt>Change</dt>
        <dd className={`num tone-${changeTone}`}>
          <span aria-hidden="true">{changeTone === 'up' ? '▲ ' : changeTone === 'down' ? '▼ ' : ''}</span>
          {formatPct(detail.priceChangePct)}
        </dd>
      </div>
      <div className="stat">
        <dt>Issued shares</dt>
        <dd className="num">{formatInt(detail.issuedSharesCount)}</dd>
      </div>
      <div className="stat">
        <dt>Market cap</dt>
        <dd className="num">{formatMoney(detail.marketCap)}</dd>
      </div>
      <div className="stat">
        <dt>Issuer cash</dt>
        <dd className="num">{formatMoney(detail.issuerCash)}</dd>
      </div>
      <div className="stat">
        <dt>Executable band</dt>
        <dd className="num">
          {detail.lowerBandPrice != null && detail.upperBandPrice != null
            ? `${formatMoney(detail.lowerBandPrice)}–${formatMoney(detail.upperBandPrice)}`
            : '—'}
        </dd>
      </div>
      <div className="stat">
        <dt>Allowed order range</dt>
        <dd className="num">
          {detail.minimumOrderPrice != null && detail.maximumOrderPrice != null
            ? `${formatMoney(detail.minimumOrderPrice)}–${formatMoney(detail.maximumOrderPrice)}`
            : '—'}
        </dd>
      </div>
    </dl>
  )
}

export function CompanyDetailTabs({
  activeTab,
  onTab,
  detail,
  prices,
  corporateCashMovements,
  corporateCashPage,
  onCorporateCashPage,
  corporateCashTableRef,
  financialHistory,
  financialHistoryPage,
  onFinancialHistoryPage,
  financialHistoryLoading,
  financialHistoryError,
  auditHistory,
  auditHistoryPage,
  onAuditHistoryPage,
  auditHistoryLoading,
  auditHistoryError,
  onSelectAudit,
  shareholders,
  orders,
  trades,
  emissions,
  investments,
  news,
  onSelectNews,
}) {
  const tabRefs = useRef({})

  function focusTab(key) {
    onTab(key)
    tabRefs.current[key]?.focus()
  }

  function onTabKeyDown(event) {
    if (event.key === 'ArrowRight' || event.key === 'ArrowLeft') {
      event.preventDefault()
      const index = DETAIL_TABS.findIndex((tab) => tab.key === activeTab)
      const delta = event.key === 'ArrowRight' ? 1 : -1
      focusTab(DETAIL_TABS[(index + delta + DETAIL_TABS.length) % DETAIL_TABS.length].key)
    } else if (event.key === 'Home') {
      event.preventDefault()
      focusTab(DETAIL_TABS[0].key)
    } else if (event.key === 'End') {
      event.preventDefault()
      focusTab(DETAIL_TABS[DETAIL_TABS.length - 1].key)
    }
  }

  return (
    <div className="detail-tabs">
      <div className="tabbar">
        <div className="tabs" role="tablist" aria-label="Company detail sections" onKeyDown={onTabKeyDown}>
          {DETAIL_TABS.map((tab) => {
            const selected = tab.key === activeTab
            return (
              <button
                key={tab.key}
                type="button"
                role="tab"
                id={`companytab-${tab.key}`}
                aria-selected={selected}
                aria-controls={`companypanel-${tab.key}`}
                tabIndex={selected ? 0 : -1}
                ref={(element) => {
                  tabRefs.current[tab.key] = element
                }}
                className={`tab${selected ? ' is-active' : ''}`}
                onClick={() => onTab(tab.key)}
              >
                {tab.label}
              </button>
            )
          })}
        </div>
      </div>
      <div
        className="tabpanel"
        role="tabpanel"
        id={`companypanel-${activeTab}`}
        aria-labelledby={`companytab-${activeTab}`}
      >
        {activeTab === 'capitalization' ? <PriceChartPanel name={detail.name} prices={prices} /> : null}
        {activeTab === 'financials' ? <CompanyFinancialsPanel financial={detail.latestFinancial} /> : null}
        {activeTab === 'financial-history' ? (
          <CompanyFinancialHistoryPanel
            history={financialHistory}
            page={financialHistoryPage}
            onPage={onFinancialHistoryPage}
            loading={financialHistoryLoading}
            error={financialHistoryError}
          />
        ) : null}
        {activeTab === 'management' ? (
          <CompanyManagementOutlookPanel financial={detail.latestFinancial} />
        ) : null}
        {activeTab === 'cash' ? (
          <CorporateCashMovementsPanel
            movements={corporateCashMovements}
            page={corporateCashPage}
            onPage={onCorporateCashPage}
            tableRef={corporateCashTableRef}
          />
        ) : null}
        {activeTab === 'shareholders' ? <ShareholdersPanel shareholders={shareholders} detail={detail} /> : null}
        {activeTab === 'orders' ? (
          <OrdersPanel orders={orders} currentPrice={detail.currentPrice} issuedShares={detail.issuedSharesCount} />
        ) : null}
        {activeTab === 'trades' ? <TradesPanel trades={trades} companyName={detail.name} /> : null}
        {activeTab === 'emissions' ? <EmissionsPanel emissions={emissions} /> : null}
        {activeTab === 'audits' ? (
          <CompanyAuditHistoryPanel
            history={auditHistory}
            page={auditHistoryPage}
            onPage={onAuditHistoryPage}
            loading={auditHistoryLoading}
            error={auditHistoryError}
            onSelectAudit={onSelectAudit}
          />
        ) : null}
        {activeTab === 'investments' ? <InvestmentsReceivedPanel investments={investments} /> : null}
        {activeTab === 'news' ? <RelatedNewsPanel news={news} onSelect={onSelectNews} /> : null}
      </div>
    </div>
  )
}

function CorporateCashMovementsPanel({ movements, page, onPage, tableRef }) {
  const items = movements.items ?? []
  const pageCount = Math.max(1, Math.ceil((movements.total ?? 0) / (movements.pageSize || CORPORATE_CASH_PAGE_SIZE)))

  return (
    <Panel title="Corporate cash movements" count={`${formatInt(movements.total ?? 0)} total`} className="panel-cash">
      {items.length === 0 ? (
        <p className="note">No issuer cash movements yet.</p>
      ) : (
        <>
          <div className="tbl-wrap" ref={tableRef}>
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
                {items.map((movement) => {
                  const presentation = corporateCashMovementPresentation(movement.type)
                  return (
                    <tr key={movement.id}>
                      <th scope="row">{presentation.label}</th>
                      <td className={`num ta-r tone-${presentation.tone}`}>
                        <span aria-label={presentation.direction}>{presentation.sign} </span>
                        {formatMoney(movement.amount)}
                      </td>
                      <td className="num ta-r">{formatInt(movement.createdInCycleNumber || movement.createdInCycleId)}</td>
                    </tr>
                  )
                })}
              </tbody>
            </table>
          </div>
          <Pager page={page} pageCount={pageCount} onPage={onPage} />
        </>
      )}
    </Panel>
  )
}

// Fund a company with a big investment as the player or the managed fund: the company mints new shares at the
// current price and hands them over in one filled deal. A minimum of 40% of market cap is required, matching the
// server rule; a successful deal refreshes the page so the new shares, cash, rating, and news show at once.
const MIN_INVESTMENT_FRACTION = 0.4

const CAPITAL_RAISE_PRESETS = [
  { label: '40%', value: 0.4 },
  { label: '50%', value: 0.5 },
  { label: '60%', value: 0.6 },
  { label: '70%', value: 0.7 },
  { label: '80%', value: 0.8 },
  { label: '90%', value: 0.9 },
  { label: '100%', value: 1 },
  { label: '150%', value: 1.5 },
  { label: '200%', value: 2 },
]

function InvestmentForm({ companyId, currentPrice, marketCap, player, onPlaced }) {
  const [amount, setAmount] = useState('')
  const [submittingActor, setSubmittingActor] = useState(null)
  const [error, setError] = useState(null)
  const [confirmation, setConfirmation] = useState(null)

  const minAmount = typeof marketCap === 'number' ? marketCap * MIN_INVESTMENT_FRACTION : null
  const actors = [{ key: 'player', label: 'Player', id: player.id, balance: player.availableBalance ?? 0 }]
  if (player.fundParticipantId != null) {
    actors.push({ key: 'fund', label: 'Managed fund', id: player.fundParticipantId, balance: player.fundAvailableBalance ?? 0 })
  }

  const value = Number(amount)
  const shares = currentPrice > 0 && value > 0 ? Math.floor(value / currentPrice) : 0

  function pickAmount(fraction) {
    if (typeof marketCap !== 'number') return
    setAmount(String(Math.round(marketCap * fraction * 100) / 100))
  }

  function disabledFor(actor) {
    if (submittingActor != null || !(value > 0) || shares < 1) return true
    if (minAmount != null && value < minAmount) return true
    if (value > actor.balance) return true
    return false
  }

  async function submitFor(actor) {
    setError(null)
    setConfirmation(null)
    setSubmittingActor(actor.key)
    try {
      const result = await api.investInCompany(companyId, { participantId: actor.id, amount: value })
      const who = actors.length > 1 ? `${actor.label} ` : ''
      setConfirmation(`${who}invested ${formatMoney(value)} for ${formatInt(result.sharesMinted)} new shares.`)
      setAmount('')
      if (onPlaced) await onPlaced()
    } catch (submitError) {
      setError(submitError.message)
    } finally {
      setSubmittingActor(null)
    }
  }

  return (
    <form
      className="modal-section player-section"
      onSubmit={(event) => {
        event.preventDefault()
        if (!disabledFor(actors[0])) submitFor(actors[0])
      }}
    >
      <span className="map-stat-label">Buy newly issued shares at {formatMoney(currentPrice)}</span>
      <div className="field">
        <span>Amount</span>
        <input
          className="select num"
          type="number"
          min="0"
          step="0.01"
          placeholder="0.00"
          aria-label="Investment amount"
          value={amount}
          onChange={(event) => setAmount(event.target.value)}
        />
        <PercentButtons options={CAPITAL_RAISE_PRESETS} ariaLabel="Set amount from a percentage of market cap" onPick={pickAmount} />
      </div>
      <p className="note note-sm">
        {minAmount != null ? `Minimum ${formatMoney(minAmount)} (40% of market cap).` : 'Minimum 40% of market cap.'}
        {shares > 0 ? ` ≈ ${formatInt(shares)} new shares.` : ''}
      </p>
      <p className="note note-sm">{actors.map((actor) => `${actor.label}: ${formatMoney(actor.balance)} available`).join(' · ')}</p>
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
            {submittingActor === actor.key ? 'Investing…' : actors.length > 1 ? `Invest as ${actor.label.toLowerCase()}` : 'Invest'}
          </button>
        ))}
      </div>
    </form>
  )
}

function InvestmentsReceivedPanel({ investments }) {
  return (
    <Panel title="Investments received" count={`${investments.length}`} className="panel-trades">
      <InvestmentsTable
        investments={investments}
        showCompany={false}
        emptyLabel="No capital-raise investments in this company yet."
      />
    </Panel>
  )
}

function RelatedNewsPanel({ news, onSelect }) {
  const [pageSize, tableRef] = useFitPageSize()
  const { pageRows, page, pageCount, setPage } = useClientTable(news, { pageSize })

  return (
    <Panel title="Related news" count={`${news.length}`} className="panel-orders-list">
      {news.length === 0 ? (
        <p className="note">No news for this company or its industry yet.</p>
      ) : (
        <>
          <div className="tbl-wrap" ref={tableRef}>
            <table className="tbl">
              <thead>
                <tr>
                  <th scope="col">Name</th>
                  <th scope="col" className="ta-r">
                    Impact
                  </th>
                </tr>
              </thead>
              <tbody>
                {pageRows.map((post) => (
                  <tr key={post.id}>
                    <th scope="row">
                      <button
                        type="button"
                        className="cell-name-btn"
                        onClick={() => onSelect(post)}
                        title={`Open ${post.title}`}
                      >
                        {post.title}
                      </button>
                    </th>
                    <td className="ta-r">
                      <NewsImpact post={post} />
                    </td>
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

function auditPeriod(audit) {
  if (!audit.evidenceAvailable) return 'Evidence unavailable'
  return `Day ${formatInt(audit.evaluationStartTradingDayNumber)}–${formatInt(audit.evaluationEndTradingDayNumber)}`
}

function auditFinancialIndicator(score, level, fallback = '—') {
  if (typeof score !== 'number') return level ?? fallback
  return level ? `${level} · ${score.toFixed(2)}` : score.toFixed(2)
}

function AuditFinancialSummary({ factors }) {
  if (!factors) {
    return <span className="note note-sm">No financial evidence</span>
  }

  return (
    <span className="audit-summary-stack">
      <span>
        <span className="audit-summary-label">Profit </span>
        <span>{auditFinancialIndicator(factors.profitabilityScore, factors.profitabilityLevel)}</span>
      </span>
      <span>
        <span className="audit-summary-label">Volatility </span>
        <span>{factors.financialVolatilityLevel ?? '—'}</span>
      </span>
      <span>
        <span className="audit-summary-label">Closure </span>
        <span>{auditFinancialIndicator(factors.closureRiskScore, factors.closureRiskLevel)}</span>
      </span>
      <span>
        <span className="audit-summary-label">Management </span>
        <span>
          {factors.managementOutlook ?? '—'}
          {typeof factors.managementConfidenceScore === 'number'
            ? ` · ${factors.managementConfidenceScore.toFixed(2)}`
            : ''}
        </span>
      </span>
    </span>
  )
}

export function CompanyAuditHistoryPanel({
  history = { items: [], total: 0, page: 1, pageSize: AUDIT_HISTORY_PAGE_SIZE },
  page = 1,
  onPage,
  loading = false,
  error = null,
  onSelectAudit,
}) {
  const items = history.items ?? []
  const pageCount = Math.max(1, Math.ceil((history.total ?? 0) / (history.pageSize || AUDIT_HISTORY_PAGE_SIZE)))

  return (
    <Panel
      title="Audits"
      count={`${formatInt(history.total ?? 0)} total`}
      className="panel-orders-list audit-history-panel"
    >
      {error && items.length === 0 ? (
        <p className="command-error" role="alert">{error}</p>
      ) : null}
      {loading && items.length === 0 ? (
        <div className="audit-history-state" role="status" aria-busy="true">
          <span className="spinner" aria-hidden="true" />
          <p className="note">Loading company audits…</p>
        </div>
      ) : null}
      {!loading && !error && items.length === 0 ? (
        <p className="note">No auditor has reviewed this company yet.</p>
      ) : null}
      {items.length > 0 ? (
        <>
          {error ? (
            <p className="command-error audit-history-notice" role="alert">
              Showing the last known audit page. {error}
            </p>
          ) : loading ? (
            <p className="note note-sm audit-history-notice" role="status">Refreshing audits…</p>
          ) : null}
          <div className="tbl-wrap audit-history-table-wrap">
            <table className="tbl audit-history-table">
              <thead>
                <tr>
                  <th scope="col">Evaluation period</th>
                  <th scope="col">Effective day</th>
                  <th scope="col">Auditor</th>
                  <th scope="col">Status</th>
                  <th scope="col" className="ta-r">Score</th>
                  <th scope="col">Financial indicators</th>
                  <th scope="col">Dividend evidence</th>
                  <th scope="col">Industry</th>
                  <th scope="col"><span className="sr-only">Details</span></th>
                </tr>
              </thead>
              <tbody>
                {items.map((audit) => (
                  <tr
                    key={audit.id}
                    data-audit-id={audit.id}
                    className="tbl-row-click audit-history-row"
                    onClick={() => onSelectAudit?.(audit.id)}
                  >
                    <th scope="row">{auditPeriod(audit)}</th>
                    <td>
                      {audit.evidenceAvailable
                        ? `Day ${formatInt(audit.effectiveTradingDayNumber)}`
                        : '—'}
                    </td>
                    <td className="cell-ellipsis">{audit.auditorName}</td>
                    <td>
                      <RatingBadge rating={audit.rating} impactPercent={audit.impactPercent} />
                    </td>
                    <td className="num ta-r">{formatInt(audit.totalScore)}</td>
                    <td>
                      {audit.evidenceAvailable
                        ? <AuditFinancialSummary factors={audit.financialFactors} />
                        : <span className="note note-sm">Evidence unavailable</span>}
                    </td>
                    <td>
                      {audit.evidenceAvailable
                        ? `${audit.latestDividendOutcome ?? 'No dividend'} · ${
                            typeof audit.dividendCoverageRatio === 'number'
                              ? `${audit.dividendCoverageRatio.toFixed(2)}×`
                              : '—'
                          }`
                        : '—'}
                    </td>
                    <td>{audit.evidenceAvailable ? audit.industryTrend ?? 'Unavailable' : '—'}</td>
                    <td className="ta-r">
                      <button
                        type="button"
                        className="btn btn-sm"
                        data-audit-id={audit.id}
                        aria-label={`View audit ${audit.id} details`}
                        onClick={(event) => {
                          event.stopPropagation()
                          onSelectAudit?.(audit.id)
                        }}
                      >
                        Details
                      </button>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
          <Pager page={page} pageCount={pageCount} onPage={onPage} />
        </>
      ) : null}
    </Panel>
  )
}

function EmissionsPanel({ emissions }) {
  return (
    <Panel title="Share emissions" count={`${emissions.length}`} className="panel-trades">
      {emissions.length === 0 ? (
        <p className="note">No free-share emissions yet.</p>
      ) : (
        <div className="tbl-wrap">
          <table className="tbl">
            <thead>
              <tr>
                <th scope="col" className="ta-r">
                  Shares
                </th>
                <th scope="col" className="ta-r">
                  Recipients
                </th>
                <th scope="col" className="ta-r">
                  Cycles ago
                </th>
              </tr>
            </thead>
            <tbody>
              {emissions.map((emission) => (
                <tr key={emission.id}>
                  <td className="num ta-r">{formatInt(emission.sharesEmitted)}</td>
                  <td className="num ta-r">{formatInt(emission.recipientCount)}</td>
                  <td className="num ta-r">{formatInt(emission.cyclesAgo)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </Panel>
  )
}

function PriceChartPanel({ name, prices }) {
  const capSnapshots = prices.filter((snapshot) => snapshot.capitalization != null)
  const values = capSnapshots.map((snapshot) => snapshot.capitalization)
  const cycles = capSnapshots.map((snapshot) => snapshot.createdInCycleNumber)
  const last = values.at(-1)
  const first = values.at(0)
  const change = values.length >= 2 ? last - first : 0
  const changePct = first ? (change / first) * 100 : 0
  const tone = toneOf(change)

  return (
    <Panel
      title={`Capitalization · ${name}`}
      count={`${prices.length} snapshot${prices.length === 1 ? '' : 's'}`}
      className="panel-chart"
    >
      {values.length < 2 ? (
        <p className="note">Not enough capitalization history yet. Start the loop or step a cycle to record trades.</p>
      ) : (
        <>
          <div className="quote">
            <strong className="quote-last num">{formatMoney(last)}</strong>
            <span className={`quote-change num tone-${tone}`}>
              <span aria-hidden="true">{tone === 'up' ? '▲' : tone === 'down' ? '▼' : '◆'}</span>
              {formatSigned(change)}
              <span className="quote-pct">
                ({change > 0 ? '+' : change < 0 ? '−' : ''}
                {Math.abs(changePct).toFixed(2)}%)
              </span>
            </span>
          </div>
          <LineChart
            values={values.slice(-PRICE_HISTORY_POINTS)}
            cycles={cycles.slice(-PRICE_HISTORY_POINTS)}
            tone={tone}
            formatValue={formatCompactMoney}
            xLabel="Cycle"
            yLabel="Capitalization"
            label={`Capitalization history for ${name}`}
            fill
          />
        </>
      )}
    </Panel>
  )
}

function OwnershipSummary({ detail }) {
  const floatPct = detail.issuedSharesCount > 0 ? detail.sharesOutstanding / detail.issuedSharesCount : 0
  const metrics = [
    { label: 'Issued shares', value: formatInt(detail.issuedSharesCount) },
    { label: 'Held by issuer', value: formatInt(detail.sharesHeldByIssuer) },
    { label: 'Outstanding', value: formatInt(detail.sharesOutstanding) },
    { label: 'Shareholders', value: formatInt(detail.shareholderCount) },
    { label: 'Float in market', value: formatPct(floatPct) },
  ]

  return (
    <div className="ownership-summary">
      <span className="map-stat-label ownership-title">Ownership</span>
      <dl className="ownership-metrics">
        {metrics.map((metric) => (
          <div className="ownership-metric" key={metric.label}>
            <dt>{metric.label}</dt>
            <dd className="num">{metric.value}</dd>
          </div>
        ))}
      </dl>
    </div>
  )
}

export function ShareholdersPanel({ shareholders, detail }) {
  const [pageSize, tableRef] = useFitPageSize()
  const { pageRows, sortKey, sortDir, toggleSort, page, pageCount, setPage } = useClientTable(shareholders, {
    pageSize,
    initialSortKey: 'shares',
    initialSortDir: 'desc',
  })

  return (
    <Panel title="Shareholders" count={`${shareholders.length}`} className="panel-holdings">
      <OwnershipSummary detail={detail} />
      {shareholders.length === 0 ? (
        <p className="note">No participant owns shares yet.</p>
      ) : (
        <>
          <div className="tbl-wrap" ref={tableRef}>
            <table className="tbl">
              <thead>
                <tr>
                  <th scope="col">Owner</th>
                  <SortHeader label="Shares" columnKey="shares" sortKey={sortKey} sortDir={sortDir} onToggle={toggleSort} />
                  <SortHeader label="% issued" columnKey="pctOfIssued" sortKey={sortKey} sortDir={sortDir} onToggle={toggleSort} />
                  <SortHeader label="Value" columnKey="marketValue" sortKey={sortKey} sortDir={sortDir} onToggle={toggleSort} />
                </tr>
              </thead>
              <tbody>
                {pageRows.map((holder) => (
                  <tr key={holder.ownerId}>
                    <th scope="row" className="cell-ellipsis">
                      <Link
                        className="cell-link"
                        to={`/traders/${holder.ownerId}`}
                        title={`Open ${holder.ownerName} trader page`}
                      >
                        {holder.ownerName}
                      </Link>
                    </th>
                    <td className="num ta-r">{formatInt(holder.shares)}</td>
                    <td className="num ta-r">{formatPct(holder.pctOfIssued)}</td>
                    <td className="num ta-r">{formatMoney(holder.marketValue)}</td>
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

function OrdersPanel({ orders, currentPrice, issuedShares }) {
  return (
    <Panel title="Recent orders" count={`last ${orders.length}`} className="panel-orders-list">
      {orders.length === 0 ? (
        <p className="note">No orders placed yet.</p>
      ) : (
        <div className="tbl-wrap">
          <table className="tbl">
            <thead>
              <tr>
                <th scope="col">Side</th>
                <th scope="col" className="ta-r">
                  Qty
                </th>
                <th scope="col" className="ta-r">
                  Limit
                </th>
                <th scope="col" className="ta-r">
                  Market price
                </th>
                <th scope="col">Status</th>
                <th scope="col">Order owner</th>
              </tr>
            </thead>
            <tbody>
              {orders.map((order) => {
                const sharePct = formatSharePct(order.quantity, issuedShares)
                const vsMarket = priceVsReference(order.limitPrice, currentPrice)
                return (
                  <tr key={order.id}>
                    <td className={`tone-${order.type === 'Buy' ? 'up' : 'down'}`}>{order.type}</td>
                    <td className="num ta-r">
                      {formatInt(order.filledQuantity)}
                      <span className="muted-sub">/{formatInt(order.quantity)}</span>
                      {sharePct ? <span className="muted-sub"> · {sharePct}</span> : null}
                    </td>
                    <td className="num ta-r">
                      {formatMoney(order.limitPrice)}
                      {vsMarket ? <span className="muted-sub"> {vsMarket}</span> : null}
                    </td>
                    <td className="num ta-r">{formatMoney(currentPrice)}</td>
                    <td>{order.status}</td>
                    <td className="cell-ellipsis">
                      {order.participantId != null ? (
                        <Link
                          className="cell-link"
                          to={`/traders/${order.participantId}`}
                          title={`Open ${order.participantName ?? 'trader'} trader page`}
                        >
                          {order.participantName ?? `#${order.participantId}`}
                        </Link>
                      ) : (
                        <span className="muted-sub">Issuer</span>
                      )}
                    </td>
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

function TradesPanel({ trades, companyName }) {
  const [selectedTrade, setSelectedTrade] = useState(null)

  // The buyer/seller cells carry their own trader links, so those stop propagation to keep navigating instead
  // of opening the trade dialog.
  const stopRow = { onClick: (event) => event.stopPropagation(), onKeyDown: (event) => event.stopPropagation() }

  return (
    <Panel title="Recent trades" count={`last ${trades.length}`} className="panel-trades">
      {trades.length === 0 ? (
        <p className="note">No settled trades yet.</p>
      ) : (
        <div className="tbl-wrap">
          <table className="tbl">
            <thead>
              <tr>
                <th scope="col" className="ta-r">
                  Qty
                </th>
                <th scope="col" className="ta-r">
                  Price
                </th>
                <th scope="col" className="ta-r">
                  Market price
                </th>
                <th scope="col" className="ta-r">
                  Total
                </th>
                <th scope="col" className="ta-r">
                  Cycle
                </th>
                <th scope="col">Buyer</th>
                <th scope="col">Seller</th>
              </tr>
            </thead>
            <tbody>
              {trades.map((trade) => {
                const vsMarket = priceVsReference(trade.price, trade.marketPriceBefore)
                return (
                  <tr
                    key={trade.id}
                    className="tbl-row-click"
                    role="button"
                    tabIndex={0}
                    aria-label={`Open details for the trade of ${formatInt(trade.quantity)} shares at ${formatMoney(trade.price)}`}
                    onClick={() => setSelectedTrade(trade)}
                    onKeyDown={(event) => {
                      if (event.key === 'Enter' || event.key === ' ') {
                        event.preventDefault()
                        setSelectedTrade(trade)
                      }
                    }}
                  >
                    <td className="num ta-r">{formatInt(trade.quantity)}</td>
                    <td className="num ta-r">
                      {formatMoney(trade.price)}
                      {vsMarket ? <span className="muted-sub"> {vsMarket}</span> : null}
                    </td>
                    <td className="num ta-r">
                      {trade.marketPriceBefore != null ? formatMoney(trade.marketPriceBefore) : '—'}
                    </td>
                    <td className="num ta-r">{formatMoney(trade.totalCost)}</td>
                    <td className="num ta-r">#{trade.createdInCycleId}</td>
                    <td className="cell-ellipsis">
                      <Link
                        className="cell-link"
                        to={`/traders/${trade.buyerId}`}
                        title={`Open ${trade.buyerName ?? 'buyer'} trader page`}
                        {...stopRow}
                      >
                        {trade.buyerName ?? `#${trade.buyerId}`}
                      </Link>
                    </td>
                    <td className="cell-ellipsis">
                      {trade.sellerId != null ? (
                        <Link
                          className="cell-link"
                          to={`/traders/${trade.sellerId}`}
                          title={`Open ${trade.sellerName ?? 'seller'} trader page`}
                          {...stopRow}
                        >
                          {trade.sellerName ?? `#${trade.sellerId}`}
                        </Link>
                      ) : (
                        <span className="muted-sub">Issuer</span>
                      )}
                    </td>
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
          companyName={companyName}
          onClose={() => setSelectedTrade(null)}
        />
      ) : null}
    </Panel>
  )
}
