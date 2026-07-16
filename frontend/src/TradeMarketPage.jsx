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

const POLL_INTERVAL_MS = 1500
const FILLED_ORDERS_PAGE_SIZE = 20
const TABS = [
  { key: 'map', label: 'Market map' },
  { key: 'activity', label: 'Orders activity' },
]

// A market-wide view that reuses the dashboard market map alongside the orders-per-cycle activity chart in a
// tab pair, with the two latest news below. Market state comes from the app shell; this page owns the rest of
// its polling. Clicking a company opens its detail route rather than a modal.
function TradeMarketPage() {
  const { market } = useOutletContext()
  const navigate = useNavigate()
  const [companies, setCompanies] = useState([])
  const [participants, setParticipants] = useState([])
  const [activity, setActivity] = useState([])
  const [news, setNews] = useState([])
  const [filledOrders, setFilledOrders] = useState({ items: [], total: 0, page: 1, pageSize: FILLED_ORDERS_PAGE_SIZE })
  const [filledOrdersPage, setFilledOrdersPage] = useState(1)
  const [investments, setInvestments] = useState([])
  const [playerHoldingCompanyIds, setPlayerHoldingCompanyIds] = useState(() => new Set())
  const [active, setActive] = useState('map')
  const tabRefs = useRef({})

  const loadAll = useCallback(async () => {
    try {
      const [companyData, participantData, activityData, newsData, filledOrderData, investmentData, playerData] = await Promise.all([
        api.getCompanies(),
        api.getParticipants(),
        api.getCycleActivity(),
        api.getNews(10),
        api.getShareTransactionsPaged(filledOrdersPage, FILLED_ORDERS_PAGE_SIZE),
        api.getInvestments(20),
        api.getPlayer(),
      ])
      setCompanies(companyData)
      setParticipants(participantData)
      setActivity(activityData)
      setNews(newsData)
      setFilledOrders(filledOrderData)
      setInvestments(investmentData ?? [])

      const pageCount = Math.max(1, Math.ceil(filledOrderData.total / FILLED_ORDERS_PAGE_SIZE))
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
  }, [filledOrdersPage])

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
          />
        ) : (
          <Panel title="Orders activity" className="panel-activity">
            <OrdersActivity activity={activity} cyclesPerDay={tradingCyclesPerDay} />
            {/* The map tab already carries the news strip; show it here so news stays visible on this tab too. */}
            <div className="trade-news">
              <LatestNews
                news={news}
                currentCycleNumber={currentCycleNumber}
                onSelectCompany={onSelectCompany}
                count={2}
              />
            </div>
          </Panel>
        )}
      </div>

      <div className="dashboard">
        <Panel
          title="Filled orders / settlements"
          count={`${formatInt(filledOrders.total)} fill${filledOrders.total === 1 ? '' : 's'}`}
          className="panel-trades"
        >
          <FilledOrdersTable
            transactions={filledOrders.items}
            total={filledOrders.total}
            page={filledOrdersPage}
            pageSize={FILLED_ORDERS_PAGE_SIZE}
            participantNameById={participantNameById}
            companyNameById={companyNameById}
            onPage={setFilledOrdersPage}
            onSelectCompany={onSelectCompany}
          />
        </Panel>
      </div>

      <div className="dashboard">
        <Panel title="Recent investments" count={`${investments.length}`} className="panel-trades">
          <InvestmentsTable investments={investments} emptyLabel="No capital-raise investments yet." />
        </Panel>
      </div>
    </main>
  )
}

export default TradeMarketPage
