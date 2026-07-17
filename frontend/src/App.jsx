import { useCallback, useEffect, useState } from 'react'
import { useOutletContext } from 'react-router-dom'
import './App.css'
import { api } from './api'
import { CompanyModal } from './CompanyModal'
import { PlayerPanel } from './PlayerPanel'
import { MarketMapPanel } from './MarketMapPanel'
import { OrderBook } from './OrderBook'
import { emptyActorHintFor, holdingByCompany, holdingCompanyIdSet, resolveActor } from './actor'

const POLL_INTERVAL_MS = 1000
const OPEN_STATUSES = new Set(['Open', 'PartiallyFilled'])

// The main dashboard owns the data poll for its market map, prioritized market updates, and order book while
// shared market state and controls stay in the app shell.
function App() {
  const { market, connected, ready, pending, actionError, runAction, actorKind } = useOutletContext()
  const [companies, setCompanies] = useState([])
  const [participants, setParticipants] = useState([])
  const [orders, setOrders] = useState([])
  const [news, setNews] = useState([])
  const [crises, setCrises] = useState([])
  const [scienceInvestigations, setScienceInvestigations] = useState([])
  const [player, setPlayer] = useState(null)
  const [playerHoldingCompanyIds, setPlayerHoldingCompanyIds] = useState(() => new Set())
  const [actorHoldingCompanyIds, setActorHoldingCompanyIds] = useState(() => new Set())
  const [actorHoldingByCompany, setActorHoldingByCompany] = useState(() => new Map())
  const [actorInvestedCompanyIds, setActorInvestedCompanyIds] = useState(() => new Set())
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

        // The order book trades as the selected actor, so its "owned" set and cost basis follow the fund when
        // the fund is selected; the map keeps the player's own set above.
        const activeId = actorKind === 'fund' ? playerData.fundParticipantId : playerData.id
        const activeHoldings = activeId == null ? [] : activeId === playerData.id ? holdings : await api.getHoldings(activeId)
        setActorHoldingCompanyIds(holdingCompanyIdSet(activeHoldings))
        setActorHoldingByCompany(holdingByCompany(activeHoldings))

        // Big-investment deals the active actor funded, so the order book can flag that actor's stakes.
        const activeInvestments = activeId == null ? [] : (await api.getParticipantInvestments(activeId)) ?? []
        setActorInvestedCompanyIds(new Set(activeInvestments.map((investment) => investment.companyId)))
      } else {
        setPlayerHoldingCompanyIds(new Set())
        setActorHoldingCompanyIds(new Set())
        setActorHoldingByCompany(new Map())
        setActorInvestedCompanyIds(new Set())
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
                <div className="dashboard">
                  <PlayerPanel
                    companies={companies}
                    onSelectCompany={setMapModalCompanyId}
                    actorKind={actorKind}
                    marketMap={
                      <MarketMapPanel
                        embedded
                        companies={companies}
                        participants={participants}
                        playerHoldingCompanyIds={playerHoldingCompanyIds}
                        lastDividendTotal={market.lastDividendTotal}
                        currentCycleNumber={market.currentCycleNumber}
                        news={news}
                        crises={crises}
                        scienceInvestigations={scienceInvestigations}
                        onSelectCompany={setMapModalCompanyId}
                      />
                    }
                    orderBook={
                      <OrderBook
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
                        actorInvestedCompanyIds={actorInvestedCompanyIds}
                        emptyActorHint={emptyActorHint}
                        onTraded={loadAll}
                      />
                    }
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
          onFavoriteChanged={(isFavorite) => {
            setCompanies((current) => current.map((company) =>
              company.id === mapModalCompany.id ? { ...company, isFavorite } : company))
          }}
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

export default App
