import { useCallback, useEffect, useRef, useState } from 'react'
import { useNavigate, useOutletContext } from 'react-router-dom'
import './App.css'
import { api } from './api'
import { Panel } from './Panel'
import { MarketMapPanel } from './MarketMapPanel'
import { OrdersActivity } from './OrdersActivity'
import { LatestNews } from './LatestNews'
import { OrderBookPanel } from './OrderBook'
import { emptyActorHintFor, holdingByCompany, holdingCompanyIdSet, resolveActor } from './actor'

const POLL_INTERVAL_MS = 1500
const OPEN_STATUSES = new Set(['Open', 'PartiallyFilled'])
const TABS = [
  { key: 'map', label: 'Market map' },
  { key: 'activity', label: 'Orders activity' },
]

// A market-wide view that reuses the dashboard market map alongside the orders-per-cycle activity chart in a
// tab pair, with the two latest news below. Market state comes from the app shell; this page owns the rest of
// its polling. Clicking a company opens its detail route rather than a modal.
function TradeMarketPage() {
  const { market, actorKind } = useOutletContext()
  const navigate = useNavigate()
  const [companies, setCompanies] = useState([])
  const [participants, setParticipants] = useState([])
  const [activity, setActivity] = useState([])
  const [news, setNews] = useState([])
  const [orders, setOrders] = useState([])
  const [player, setPlayer] = useState(null)
  const [playerHoldingCompanyIds, setPlayerHoldingCompanyIds] = useState(() => new Set())
  const [actorHoldingCompanyIds, setActorHoldingCompanyIds] = useState(() => new Set())
  const [actorHoldingByCompany, setActorHoldingByCompany] = useState(() => new Map())
  const [active, setActive] = useState('map')
  const tabRefs = useRef({})

  const loadAll = useCallback(async () => {
    try {
      const [companyData, participantData, activityData, newsData, orderData, playerData] = await Promise.all([
        api.getCompanies(),
        api.getParticipants(),
        api.getCycleActivity(),
        api.getNews(10),
        api.getOrders('open'),
        api.getPlayer(),
      ])
      setCompanies(companyData)
      setParticipants(participantData)
      setActivity(activityData)
      setNews(newsData)
      setOrders(orderData)
      setPlayer(playerData)

      if (playerData) {
        const holdings = await api.getHoldings(playerData.id)
        const playerHeld = holdingCompanyIdSet(holdings)
        setPlayerHoldingCompanyIds(playerHeld)

        const activeId = actorKind === 'fund' ? playerData.fundParticipantId : playerData.id
        const activeHoldings = activeId == null ? [] : activeId === playerData.id ? holdings : await api.getHoldings(activeId)
        setActorHoldingCompanyIds(holdingCompanyIdSet(activeHoldings))
        setActorHoldingByCompany(holdingByCompany(activeHoldings))
      } else {
        setPlayerHoldingCompanyIds(new Set())
        setActorHoldingCompanyIds(new Set())
        setActorHoldingByCompany(new Map())
      }
    } catch {
      // Keep the last known state when a refresh fails; the shell surfaces the offline status.
    }
  }, [actorKind])

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
  const onSelectCompany = (companyId) => navigate(`/companies/${companyId}`)

  const participantNameById = new Map(participants.map((participant) => [participant.id, participant.name]))
  const bankruptParticipantIds = new Set(
    participants.filter((participant) => participant.isBankrupt).map((participant) => participant.id),
  )
  const companyNameById = new Map(companies.map((company) => [company.id, company.name]))
  const companyPriceById = new Map(companies.map((company) => [company.id, company.currentPrice]))
  const companySharesById = new Map(companies.map((company) => [company.id, company.issuedSharesCount]))
  const companyById = new Map(companies.map((company) => [company.id, company]))
  const openOrders = orders.filter((order) => OPEN_STATUSES.has(order.status))
  const actor = resolveActor(player, actorKind)
  const emptyActorHint = emptyActorHintFor(player, actorKind)

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
            <OrdersActivity activity={activity} />
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
        <OrderBookPanel
          orders={openOrders}
          participantNameById={participantNameById}
          bankruptParticipantIds={bankruptParticipantIds}
          companyNameById={companyNameById}
          companyPriceById={companyPriceById}
          companySharesById={companySharesById}
          companyById={companyById}
          actor={actor}
          actorHoldingCompanyIds={actorHoldingCompanyIds}
          actorHoldingByCompany={actorHoldingByCompany}
          emptyActorHint={emptyActorHint}
          onSelectCompany={onSelectCompany}
          onTraded={loadAll}
        />
      </div>
    </main>
  )
}

export default TradeMarketPage
