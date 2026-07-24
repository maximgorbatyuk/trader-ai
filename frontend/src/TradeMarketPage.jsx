import { useCallback, useEffect, useRef, useState } from 'react'
import { useNavigate, useOutletContext } from 'react-router-dom'
import './App.css'
import { api } from './api'
import { Panel } from './Panel'
import { MarketMapPanel } from './MarketMapPanel'
import { OrdersActivity } from './OrdersActivity'
import { LatestNews } from './LatestNews'
import { FilledOrdersTable } from './FilledOrdersTable'
import { InvestmentsTable } from './InvestmentsTable'
import { holdingCompanyIdSet } from './actor'
import { formatInt } from './format'
import { useFitPageSize } from './useFitPageSize'
import { PortfolioAuditSummaryModal } from './PortfolioAuditSummaryModal'

const POLL_INTERVAL_MS = 1500
const TABS = [
  { key: 'map', label: 'Market map' },
  { key: 'activity', label: 'Orders activity' },
  { key: 'fills', label: 'Filled orders / settlements' },
  { key: 'investments', label: 'Recent investments' },
]

// A market-wide view that gathers the market map, the orders-per-cycle activity chart, the settlement feed, and
// recent investments into a single tab strip so only one lives on screen at a time. The filled-orders and
// investments tables size their fetch to the viewport so the active tab fills the browser height without a page
// scroll. Market state comes from the app shell; this page owns the rest of its polling. Clicking a company
// opens its detail route rather than a modal.
function TradeMarketPage() {
  const { market } = useOutletContext()
  const navigate = useNavigate()
  const [companies, setCompanies] = useState([])
  const [participants, setParticipants] = useState([])
  const [activity, setActivity] = useState([])
  const [news, setNews] = useState([])
  const [filledOrders, setFilledOrders] = useState({ items: [], total: 0 })
  const [filledOrdersPage, setFilledOrdersPage] = useState(1)
  const [investments, setInvestments] = useState([])
  const [playerHoldingCompanyIds, setPlayerHoldingCompanyIds] = useState(() => new Set())
  const [active, setActive] = useState('map')
  const [selectedPortfolioAuditSummaryId, setSelectedPortfolioAuditSummaryId] = useState(null)
  const tabRefs = useRef({})
  const [filledPageSize, fillsTableRef] = useFitPageSize()
  const [investmentsCount, investmentsTableRef] = useFitPageSize()

  const loadAll = useCallback(async () => {
    try {
      const [companyData, participantData, activityData, newsData, filledOrderData, investmentData, playerData] = await Promise.all([
        api.getCompanies(),
        api.getParticipants(),
        api.getCycleActivity(),
        api.getNews(10),
        api.getShareTransactionsPaged(filledOrdersPage, filledPageSize),
        api.getInvestments(investmentsCount),
        api.getPlayer(),
      ])
      setCompanies(companyData)
      setParticipants(participantData)
      setActivity(activityData)
      setNews(newsData)
      setFilledOrders(filledOrderData)
      setInvestments(investmentData ?? [])

      const pageCount = Math.max(1, Math.ceil(filledOrderData.total / filledPageSize))
      if (filledOrdersPage > pageCount) {
        setFilledOrdersPage(pageCount)
      }

      if (playerData) {
        const holdings = await api.getHoldings(playerData.id)
        setPlayerHoldingCompanyIds(holdingCompanyIdSet(holdings))
      } else {
        setPlayerHoldingCompanyIds(new Set())
      }
    } catch {
      // Keep the last known state when a refresh fails; the shell surfaces the offline status.
    }
  }, [filledOrdersPage, filledPageSize, investmentsCount])

  useEffect(() => {
    const initialId = setTimeout(loadAll, 0)
    const intervalId = setInterval(loadAll, POLL_INTERVAL_MS)
    return () => {
      clearTimeout(initialId)
      clearInterval(intervalId)
    }
  }, [loadAll])

  function focusTab(key) {
    setActive(key)
    tabRefs.current[key]?.focus()
  }

  function onTabKeyDown(event) {
    const index = TABS.findIndex((tab) => tab.key === active)
    if (event.key === 'ArrowRight' || event.key === 'ArrowLeft') {
      event.preventDefault()
      const step = event.key === 'ArrowRight' ? 1 : -1
      focusTab(TABS[(index + step + TABS.length) % TABS.length].key)
    } else if (event.key === 'Home') {
      event.preventDefault()
      focusTab(TABS[0].key)
    } else if (event.key === 'End') {
      event.preventDefault()
      focusTab(TABS[TABS.length - 1].key)
    }
  }

  const currentCycleNumber = market?.currentCycleNumber ?? null
  const tradingCyclesPerDay =
    market?.tradingCycleNumber != null && market?.remainingTradingCycles != null
      ? market.tradingCycleNumber + market.remainingTradingCycles
      : null
  const onSelectCompany = (companyId) => navigate(`/companies/${companyId}`)

  const participantNameById = new Map(participants.map((participant) => [participant.id, participant.name]))
  const companyNameById = new Map(companies.map((company) => [company.id, company.name]))

  return (
    <main className="main">
      <div className="tabbar">
        <div className="tabs" role="tablist" aria-label="Trade market sections" onKeyDown={onTabKeyDown}>
          {TABS.map((tab) => {
            const selected = tab.key === active
            return (
              <button
                key={tab.key}
                type="button"
                role="tab"
                id={`trademarket-tab-${tab.key}`}
                aria-selected={selected}
                aria-controls={`trademarket-panel-${tab.key}`}
                tabIndex={selected ? 0 : -1}
                ref={(element) => {
                  tabRefs.current[tab.key] = element
                }}
                className={`tab${selected ? ' is-active' : ''}`}
                onClick={() => setActive(tab.key)}
              >
                {tab.label}
              </button>
            )
          })}
        </div>
      </div>

      <div
        className="dashboard"
        role="tabpanel"
        id={`trademarket-panel-${active}`}
        aria-labelledby={`trademarket-tab-${active}`}
      >
        {active === 'map' ? (
          <MarketMapPanel
            companies={companies}
            participants={participants}
            playerHoldingCompanyIds={playerHoldingCompanyIds}
            lastDividendTotal={market?.lastDividendTotal ?? 0}
            currentCycleNumber={currentCycleNumber}
            news={news}
            onSelectCompany={onSelectCompany}
            onSelectPortfolioAuditSummary={setSelectedPortfolioAuditSummaryId}
          />
        ) : active === 'activity' ? (
          <Panel title="Orders activity" className="panel-activity">
            <OrdersActivity activity={activity} cyclesPerDay={tradingCyclesPerDay} />
            {/* The map tab already carries the news strip; show it here so news stays visible on this tab too. */}
            <div className="trade-news">
              <LatestNews
                news={news}
                currentCycleNumber={currentCycleNumber}
                onSelectCompany={onSelectCompany}
                onSelectPortfolioAuditSummary={setSelectedPortfolioAuditSummaryId}
                count={2}
              />
            </div>
          </Panel>
        ) : active === 'fills' ? (
          <Panel
            title="Filled orders / settlements"
            count={`${formatInt(filledOrders.total)} fill${filledOrders.total === 1 ? '' : 's'}`}
            className="panel-trades"
          >
            <div ref={fillsTableRef}>
              <FilledOrdersTable
                transactions={filledOrders.items}
                total={filledOrders.total}
                page={filledOrdersPage}
                pageSize={filledPageSize}
                participantNameById={participantNameById}
                companyNameById={companyNameById}
                onPage={setFilledOrdersPage}
                onSelectCompany={onSelectCompany}
              />
            </div>
          </Panel>
        ) : (
          <Panel title="Recent investments" count={`${investments.length}`} className="panel-trades">
            <div ref={investmentsTableRef}>
              <InvestmentsTable investments={investments} emptyLabel="No capital-raise investments yet." />
            </div>
          </Panel>
        )}
      </div>
      {selectedPortfolioAuditSummaryId != null ? (
        <PortfolioAuditSummaryModal
          summaryId={selectedPortfolioAuditSummaryId}
          onClose={() => setSelectedPortfolioAuditSummaryId(null)}
        />
      ) : null}
    </main>
  )
}

export default TradeMarketPage
