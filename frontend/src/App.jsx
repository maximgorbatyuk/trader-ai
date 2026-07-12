import { useCallback, useEffect, useState } from 'react'
import { Link, useOutletContext } from 'react-router-dom'
import './App.css'
import { api } from './api'
import { CompanyModal } from './CompanyModal'
import { PlayerPanel } from './PlayerPanel'
import { MarketMapPanel } from './MarketMapPanel'
import { OrderBookPanel } from './OrderBook'
import { emptyActorHintFor, holdingCompanyIdSet, resolveActor } from './actor'

const POLL_INTERVAL_MS = 1000
const OPEN_STATUSES = new Set(['Open', 'PartiallyFilled'])

// The main dashboard: market map (with the two latest news), the player control surface, and the order book.
// The market state, connection, and control actions come from the app shell through the outlet context; this
// page owns only its own data poll.
function App() {
  const { market, connected, ready, pending, actionError, runAction, actorKind, setActorKind } = useOutletContext()
  const [companies, setCompanies] = useState([])
  const [participants, setParticipants] = useState([])
  const [orders, setOrders] = useState([])
  const [news, setNews] = useState([])
  const [crises, setCrises] = useState([])
  const [scienceInvestigations, setScienceInvestigations] = useState([])
  const [player, setPlayer] = useState(null)
  const [playerHoldingCompanyIds, setPlayerHoldingCompanyIds] = useState(() => new Set())
  const [actorHoldingCompanyIds, setActorHoldingCompanyIds] = useState(() => new Set())
  const [mapModalCompanyId, setMapModalCompanyId] = useState(null)

  const loadAll = useCallback(async () => {
    try {
      const [companyData, participantData, orderData, newsData, crisisData, scienceData, playerData] =
        await Promise.all([
          api.getCompanies(),
          api.getParticipants(),
          api.getOrders('open'),
          api.getNews(10),
          api.getCrises(10),
          api.getScienceInvestigations(10),
          api.getPlayer(),
        ])

      setCompanies(companyData)
      setParticipants(participantData)
      setOrders(orderData)
      setNews(newsData)
      setCrises(crisisData)
      setScienceInvestigations(scienceData)
      setPlayer(playerData)

      if (playerData) {
        const holdings = await api.getHoldings(playerData.id)
        const playerHeld = holdingCompanyIdSet(holdings)
        setPlayerHoldingCompanyIds(playerHeld)

        // The order book trades as the selected actor, so its "owned" set follows the fund when the fund is
        // selected; the map keeps the player's own set above.
        const activeId = actorKind === 'fund' ? playerData.fundParticipantId : playerData.id
        if (activeId == null) {
          setActorHoldingCompanyIds(new Set())
        } else if (activeId === playerData.id) {
          setActorHoldingCompanyIds(playerHeld)
        } else {
          setActorHoldingCompanyIds(holdingCompanyIdSet(await api.getHoldings(activeId)))
        }
      } else {
        setPlayerHoldingCompanyIds(new Set())
        setActorHoldingCompanyIds(new Set())
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

  const participantNameById = new Map(participants.map((participant) => [participant.id, participant.name]))
  const bankruptParticipantIds = new Set(
    participants.filter((participant) => participant.isBankrupt).map((participant) => participant.id),
  )
  const companyNameById = new Map(companies.map((company) => [company.id, company.name]))
  const companyPriceById = new Map(companies.map((company) => [company.id, company.currentPrice]))
  const companySharesById = new Map(companies.map((company) => [company.id, company.issuedSharesCount]))
  const companyById = new Map(companies.map((company) => [company.id, company]))
  const openOrders = orders.filter((order) => OPEN_STATUSES.has(order.status))
  const mapModalCompany = companies.find((company) => company.id === mapModalCompanyId) ?? null
  const actor = resolveActor(player, actorKind)
  const emptyActorHint = emptyActorHintFor(player, actorKind)

  return (
    <>
      <main className="main">
        {!ready ? (
          <ConnectingState />
        ) : (
          <>
            {!connected ? <OfflineBanner /> : null}

            {actionError ? (
              <div className="banner" role="alert">
                <strong>Action failed.</strong>
                <span>{actionError}</span>
              </div>
            ) : null}

            {market === null && connected ? <SeedPanel pending={pending} runAction={runAction} /> : null}

            {market !== null ? (
              <>
                <CrisisBanner crises={crises} currentCycleNumber={market.currentCycleNumber} />
                <ScienceBanner
                  investigations={scienceInvestigations}
                  currentCycleNumber={market.currentCycleNumber}
                />

                <div className="dashboard">
                  <MarketMapPanel
                    companies={companies}
                    participants={participants}
                    playerHoldingCompanyIds={playerHoldingCompanyIds}
                    lastDividendTotal={market.lastDividendTotal}
                    currentCycleNumber={market.currentCycleNumber}
                    news={news}
                    onSelectCompany={setMapModalCompanyId}
                  />

                  <PlayerPanel
                    companies={companies}
                    onSelectCompany={setMapModalCompanyId}
                    actorKind={actorKind}
                    setActorKind={setActorKind}
                  />

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
                    emptyActorHint={emptyActorHint}
                    onSelectCompany={setMapModalCompanyId}
                    onTraded={loadAll}
                  />
                </div>
              </>
            ) : null}
          </>
        )}
      </main>

      {mapModalCompany ? (
        <CompanyModal
          company={mapModalCompany}
          onClose={() => setMapModalCompanyId(null)}
        />
      ) : null}
    </>
  )
}

function ConnectingState() {
  return (
    <section className="placeholder" aria-busy="true">
      <span className="spinner" aria-hidden="true" />
      <p>Connecting to the trader-ai backend…</p>
    </section>
  )
}

function OfflineBanner() {
  return (
    <div className="banner" role="alert">
      <strong>Backend offline.</strong>
      <span>Showing the last known state. Retrying every {POLL_INTERVAL_MS / 1000}s.</span>
    </div>
  )
}

function SeedPanel({ pending, runAction }) {
  return (
    <section className="panel seed">
      <div className="seed-body">
        <strong>No market running</strong>
        <p>Seed the demo market to create companies, participants, and the first cycle.</p>
        <button className="btn btn-primary" disabled={pending} onClick={() => runAction(api.seedMarket)}>
          Seed demo market
        </button>
      </div>
    </section>
  )
}

// A crisis is only shown in the banner while it is still fresh, so an old shock does not linger at the top.
const CRISIS_RECENT_CYCLES = 15

function crisisDropRange(crisis) {
  const percents = crisis.industries.map((link) => Number(link.impactPercent))
  if (percents.length === 0) return null
  return { min: Math.min(...percents), max: Math.max(...percents) }
}

function formatDropRange(crisis) {
  const range = crisisDropRange(crisis)
  if (!range) return ''
  return range.min === range.max
    ? `−${range.max.toFixed(1)}%`
    : `−${range.min.toFixed(1)}% to −${range.max.toFixed(1)}%`
}

function sectorLabel(count) {
  return count === 1 ? 'sector' : 'sectors'
}

function scienceGainRange(investigation) {
  const percents = investigation.industries.map((link) => Number(link.impactPercent))
  if (percents.length === 0) return null
  return { min: Math.min(...percents), max: Math.max(...percents) }
}

function formatGainRange(investigation) {
  const range = scienceGainRange(investigation)
  if (!range) return ''
  return range.min === range.max
    ? `+${range.max.toFixed(1)}%`
    : `+${range.min.toFixed(1)}% to +${range.max.toFixed(1)}%`
}

// A prominent alert for the most recent crisis, shown only while it is recent relative to the current cycle.
function CrisisBanner({ crises, currentCycleNumber }) {
  const latest = crises[0]
  if (!latest) return null
  if (
    currentCycleNumber != null &&
    currentCycleNumber - latest.triggeredInCycleNumber > CRISIS_RECENT_CYCLES
  ) {
    return null
  }

  const sectorCount = latest.industries.length
  return (
    <div className="crisis-banner" role="alert">
      <span className="crisis-banner-mark" aria-hidden="true">
        ⚠
      </span>
      <div className="crisis-banner-body">
        <p className="crisis-banner-head">
          <span className="crisis-scope">{latest.scope} crisis</span>
          <Link className="crisis-banner-title" to={`/crises/${latest.id}`}>
            {latest.title}
          </Link>
        </p>
        <p className="crisis-banner-meta num">
          {sectorCount} {sectorLabel(sectorCount)} · {formatDropRange(latest)} · cycle{' '}
          {latest.triggeredInCycleNumber}
        </p>
      </div>
    </div>
  )
}

// A green counterpart to the crisis banner for the most recent science investigation, shown only while it
// is still recent relative to the current cycle so an old breakthrough does not linger at the top.
function ScienceBanner({ investigations, currentCycleNumber }) {
  const latest = investigations[0]
  if (!latest) return null
  if (
    currentCycleNumber != null &&
    currentCycleNumber - latest.triggeredInCycleNumber > CRISIS_RECENT_CYCLES
  ) {
    return null
  }

  const sectorCount = latest.industries.length
  return (
    <div className="science-banner" role="status">
      <span className="science-banner-mark" aria-hidden="true">
        🔬
      </span>
      <div className="crisis-banner-body">
        <p className="crisis-banner-head">
          <span className="science-scope">Science breakthrough</span>
          <span className="crisis-banner-title">{latest.title}</span>
        </p>
        <p className="science-banner-meta num">
          {sectorCount} {sectorLabel(sectorCount)} · {formatGainRange(latest)} · cycle{' '}
          {latest.triggeredInCycleNumber}
        </p>
      </div>
    </div>
  )
}

export default App
