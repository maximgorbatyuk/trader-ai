import { useCallback, useEffect, useRef, useState } from 'react'
import './App.css'
import { api } from './api'
import { formatCompactMoney, formatInt, formatMoney, toneOf } from './format'
import { Panel } from './Panel'
import { CompanyModal } from './CompanyModal'
import { CompanyCombobox } from './CompanyCombobox'
import { PlayerModal, PlayerPanel } from './PlayerModal'
import { TradersTable } from './TradersTable'
import { CompaniesTable } from './CompaniesTable'
import { ParticipantSummaryModal } from './ParticipantSummaryModal'

const POLL_INTERVAL_MS = 1000
const OPEN_STATUSES = new Set(['Open', 'PartiallyFilled'])
const REPOSITORY_URL = 'https://github.com/maximgorbatyuk/trader-ai'
const FOOTER_LINK_GROUPS = [
  [
    { label: 'Concept', href: `${REPOSITORY_URL}/blob/main/docs/domain.md` },
    { label: 'About', href: `${REPOSITORY_URL}#trader-ai` },
  ],
  [
    { label: 'Github', href: REPOSITORY_URL },
    { label: 'Issues', href: `${REPOSITORY_URL}/issues` },
  ],
]

// A null participant is the share issuer's own offering (seeded company sell orders).
function traderName(id, byId) {
  if (id == null) return 'Issuer'
  return byId.get(id) ?? `#${id}`
}

function App() {
  const [ready, setReady] = useState(false)
  const [connected, setConnected] = useState(false)
  const [market, setMarket] = useState(null)
  const [companies, setCompanies] = useState([])
  const [participants, setParticipants] = useState([])
  const [orders, setOrders] = useState([])
  const [transactions, setTransactions] = useState([])
  const [cycleActivity, setCycleActivity] = useState([])
  const [news, setNews] = useState([])
  const [crises, setCrises] = useState([])
  const [scienceInvestigations, setScienceInvestigations] = useState([])
  const [bankruptcies, setBankruptcies] = useState([])
  const [player, setPlayer] = useState(null)
  const [playerHoldingCompanyIds, setPlayerHoldingCompanyIds] = useState(() => new Set())
  const [mapModalCompanyId, setMapModalCompanyId] = useState(null)
  const [playerModalOpen, setPlayerModalOpen] = useState(false)
  const [summaryParticipant, setSummaryParticipant] = useState(null)
  const [pending, setPending] = useState(false)
  const [actionError, setActionError] = useState(null)

  const loadAll = useCallback(async () => {
    try {
      const [
        marketData,
        companyData,
        participantData,
        orderData,
        activityData,
        transactionData,
        newsData,
        crisisData,
        scienceData,
        bankruptcyData,
        playerData,
      ] = await Promise.all([
        api.getMarket(),
        api.getCompanies(),
        api.getParticipants(),
        api.getOrders('open'),
        api.getCycleActivity(),
        api.getShareTransactions(50),
        api.getNews(10),
        api.getCrises(10),
        api.getScienceInvestigations(10),
        api.getBankruptcies(10),
        api.getPlayer(),
      ])

      setMarket(marketData)
      setCompanies(companyData)
      setParticipants(participantData)
      setOrders(orderData)
      setCycleActivity(activityData)
      setTransactions(transactionData)
      setNews(newsData)
      setCrises(crisisData)
      setScienceInvestigations(scienceData)
      setBankruptcies(bankruptcyData)
      setPlayer(playerData)

      if (playerData) {
        const holdings = await api.getHoldings(playerData.id)
        setPlayerHoldingCompanyIds(
          new Set(holdings.filter((holding) => holding.shares > 0).map((holding) => holding.companyId)),
        )
      } else {
        setPlayerHoldingCompanyIds(new Set())
      }

      setConnected(true)
    } catch {
      setConnected(false)
    } finally {
      setReady(true)
    }
  }, [])

  useEffect(() => {
    const initialId = setTimeout(loadAll, 0)
    const intervalId = setInterval(loadAll, POLL_INTERVAL_MS)
    return () => {
      clearTimeout(initialId)
      clearInterval(intervalId)
    }
  }, [loadAll])

  async function runAction(action) {
    setPending(true)
    setActionError(null)
    try {
      await action()
      await loadAll()
    } catch (error) {
      setActionError(error.message)
    } finally {
      setPending(false)
    }
  }

  async function resetMarket() {
    await api.resetMarket()

    setMapModalCompanyId(null)
  }

  const participantNameById = new Map(participants.map((participant) => [participant.id, participant.name]))
  const bankruptParticipantIds = new Set(participants.filter((participant) => participant.isBankrupt).map((participant) => participant.id))
  const companyNameById = new Map(companies.map((company) => [company.id, company.name]))
  const companyPriceById = new Map(companies.map((company) => [company.id, company.currentPrice]))
  const openOrders = orders.filter((order) => OPEN_STATUSES.has(order.status))
  const mapModalCompany = companies.find((company) => company.id === mapModalCompanyId) ?? null

  return (
    <div className="app">
      <TopBar
        connected={connected}
        ready={ready}
        market={market}
        pending={pending}
        runAction={runAction}
        resetMarket={resetMarket}
        onOpenPlayer={() => setPlayerModalOpen(true)}
      />

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

            {market === null && connected ? (
              <SeedPanel pending={pending} runAction={runAction} />
            ) : null}

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
                    lastDividendTotal={market.lastDividendTotal}
                    currentCycleNumber={market.currentCycleNumber}
                    latestNews={news[0] ?? null}
                    onSelectCompany={setMapModalCompanyId}
                  />

                  <DashboardTabs
                    companies={companies}
                    participants={participants}
                    transactions={transactions}
                    activity={cycleActivity}
                    participantNameById={participantNameById}
                    companyNameById={companyNameById}
                    onSelectCompany={setMapModalCompanyId}
                    onSelectTrader={setSummaryParticipant}
                  />

                  <OrderBookPanel
                    orders={openOrders}
                    participantNameById={participantNameById}
                    bankruptParticipantIds={bankruptParticipantIds}
                    companyNameById={companyNameById}
                    companyPriceById={companyPriceById}
                    player={player}
                    playerHoldingCompanyIds={playerHoldingCompanyIds}
                    onTraded={loadAll}
                  />

                  <NewswirePanel
                    news={news}
                    crises={crises}
                    scienceInvestigations={scienceInvestigations}
                    bankruptcies={bankruptcies}
                    companies={companies}
                    onPublished={loadAll}
                  />
                </div>
              </>
            ) : null}
          </>
        )}
      </main>

      <Footer />

      {mapModalCompany ? (
        <CompanyModal
          company={mapModalCompany}
          participantNameById={participantNameById}
          onClose={() => setMapModalCompanyId(null)}
        />
      ) : null}

      {playerModalOpen ? (
        <PlayerModal companies={companies} onClose={() => setPlayerModalOpen(false)} />
      ) : null}

      {summaryParticipant ? (
        <ParticipantSummaryModal participant={summaryParticipant} onClose={() => setSummaryParticipant(null)} />
      ) : null}
    </div>
  )
}

function TopBar({ connected, ready, market, pending, runAction, resetMarket, onOpenPlayer }) {
  return (
    <header className="topbar">
      <a className="brand" href="/" aria-label="Trader AI dashboard">
        <span className="brand-mark" aria-hidden="true">
          TA
        </span>
        <span className="brand-name">Trader&nbsp;AI</span>
        <span className="brand-tag" aria-hidden="true">
          Market Simulator
        </span>
      </a>
      <div className="topbar-status">
        {market ? (
          <Controls market={market} pending={pending} runAction={runAction} resetMarket={resetMarket} />
        ) : null}
        {market ? (
          <button type="button" className="btn" onClick={onOpenPlayer}>
            Player
          </button>
        ) : null}
        {market ? <CycleBadge cycleNumber={market.currentCycleNumber} /> : null}
        {market ? <StatusBadge status={market.status} /> : null}
        <ConnPill connected={connected} ready={ready} />
      </div>
    </header>
  )
}

function Controls({ market, pending, runAction, resetMarket }) {
  const running = market.status === 'Running'
  const [confirmingReset, setConfirmingReset] = useState(false)

  useEffect(() => {
    if (!confirmingReset) return undefined

    const timer = setTimeout(() => setConfirmingReset(false), 5000)
    return () => clearTimeout(timer)
  }, [confirmingReset])

  function handleResetMarket() {
    if (!confirmingReset) {
      setConfirmingReset(true)
      return
    }

    setConfirmingReset(false)
    runAction(resetMarket)
  }

  return (
    <div className="controls" role="group" aria-label="Market controls">
      <button
        className="btn"
        disabled={pending || running}
        title={running ? 'Stop the loop to step a cycle by hand' : 'Run one decision-and-match cycle'}
        onClick={() => runAction(api.stepCycle)}
      >
        Step once
      </button>
      {running ? (
        <button className="btn btn-primary" disabled={pending} onClick={() => runAction(api.pauseMarket)}>
          Pause loop
        </button>
      ) : (
        <button className="btn btn-primary" disabled={pending} onClick={() => runAction(api.startMarket)}>
          Start loop
        </button>
      )}
      <button
        className={`btn btn-reset${confirmingReset ? ' btn-reset-armed' : ''}`}
        disabled={pending}
        title={confirmingReset ? 'Click again to erase and reseed the demo database' : 'Erase and reseed the demo database'}
        onClick={handleResetMarket}
      >
        {confirmingReset ? 'Confirm reset' : 'Reset DB'}
      </button>
    </div>
  )
}

function Footer() {
  return (
    <footer className="footer">
      <div className="footer-brand">
        <p className="footer-title">Trader AI</p>
        <p className="footer-subtitle">
          Made with ❤️, coffee and claude by (c){' '}
          <a href="https://github.com/maximgorbatyuk" target="_blank" rel="noreferrer">
            maximgorbatyuk
          </a>
        </p>
      </div>

      {FOOTER_LINK_GROUPS.map((links, index) => (
        <nav
          className="footer-links"
          aria-label={index === 0 ? 'Project links' : 'Repository links'}
          key={index === 0 ? 'project' : 'repository'}
        >
          <ul>
            {links.map((link) => (
              <li key={link.label}>
                <a href={link.href} target="_blank" rel="noreferrer">
                  {link.label}
                </a>
              </li>
            ))}
          </ul>
        </nav>
      ))}
    </footer>
  )
}

function ConnPill({ connected, ready }) {
  const state = !ready ? 'pending' : connected ? 'live' : 'down'
  const label = !ready ? 'Connecting' : connected ? 'Backend live' : 'Backend offline'

  return (
    <span className={`conn conn-${state}`} role="status">
      <span className="conn-dot" aria-hidden="true" />
      {label}
    </span>
  )
}

// Null until the market has run its first cycle, so the badge stays hidden rather than showing "#—".
function CycleBadge({ cycleNumber }) {
  if (cycleNumber == null) return null

  return (
    <span className="cycle-badge" role="status" aria-label={`Current cycle ${cycleNumber}`}>
      <span className="cycle-badge-label" aria-hidden="true">
        Cycle
      </span>
      <span className="cycle-badge-value">#{formatInt(cycleNumber)}</span>
    </span>
  )
}

const STATUS_TONE = {
  Running: 'up',
  Paused: 'attention',
  Completed: 'muted',
  NotStarted: 'muted',
}

function StatusBadge({ status }) {
  const tone = STATUS_TONE[status] ?? 'muted'
  return <span className={`pill pill-${tone}`}>{status}</span>
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

const DASHBOARD_TABS = [
  { key: 'player', label: 'Player' },
  { key: 'traders', label: 'Traders' },
  { key: 'companies', label: 'Companies' },
  { key: 'tape', label: 'Trade tape' },
  { key: 'activity', label: 'Market activity' },
]

// Tabbed block under the market map that consolidates the player control surface with the traders,
// companies, and trade-tape tables. A WCAG tablist (roving tabindex, arrow-key navigation) drives which
// body renders; the active tab reads by weight and an ink underline rather than colour alone.
function DashboardTabs({ companies, participants, transactions, activity, participantNameById, companyNameById, onSelectCompany, onSelectTrader }) {
  const [active, setActive] = useState('player')
  const tabRefs = useRef({})

  function focusTab(key) {
    setActive(key)
    tabRefs.current[key]?.focus()
  }

  function onTabKeyDown(event) {
    const index = DASHBOARD_TABS.findIndex((tab) => tab.key === active)
    if (event.key === 'ArrowRight' || event.key === 'ArrowLeft') {
      event.preventDefault()
      const step = event.key === 'ArrowRight' ? 1 : -1
      const next = DASHBOARD_TABS[(index + step + DASHBOARD_TABS.length) % DASHBOARD_TABS.length]
      focusTab(next.key)
    } else if (event.key === 'Home') {
      event.preventDefault()
      focusTab(DASHBOARD_TABS[0].key)
    } else if (event.key === 'End') {
      event.preventDefault()
      focusTab(DASHBOARD_TABS[DASHBOARD_TABS.length - 1].key)
    }
  }

  return (
    <article className="panel panel-tabs">
      <div className="panel-head">
        <div className="tabs" role="tablist" aria-label="Dashboard sections" onKeyDown={onTabKeyDown}>
          {DASHBOARD_TABS.map((tab) => {
            const selected = tab.key === active
            return (
              <button
                key={tab.key}
                type="button"
                role="tab"
                id={`dashtab-${tab.key}`}
                aria-selected={selected}
                aria-controls={`dashpanel-${tab.key}`}
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
      <div className="tabpanel" role="tabpanel" id={`dashpanel-${active}`} aria-labelledby={`dashtab-${active}`}>
        {active === 'player' ? <PlayerPanel companies={companies} onSelectCompany={onSelectCompany} /> : null}
        {active === 'traders' ? <TradersTable participants={participants} onSelectTrader={onSelectTrader} /> : null}
        {active === 'companies' ? <CompaniesTable companies={companies} onSelectCompany={onSelectCompany} /> : null}
        {active === 'tape' ? (
          <TradeTapeTable
            transactions={transactions}
            participantNameById={participantNameById}
            companyNameById={companyNameById}
          />
        ) : null}
        {active === 'activity' ? <ActivityBody activity={activity} /> : null}
      </div>
    </article>
  )
}

const NEWS_DIRECTION = {
  Increase: { tone: 'up', glyph: '▲', sign: '+' },
  Decrease: { tone: 'down', glyph: '▼', sign: '−' },
}

// A published event's market effect: none, or a signed percent move tied to a company or list of industries.
function NewsImpact({ post }) {
  if (post.scope === 'None' || !post.direction) {
    return <span className="news-impact news-impact-none">No market impact</span>
  }

  const direction = NEWS_DIRECTION[post.direction] ?? NEWS_DIRECTION.Increase
  const target = post.scope === 'Company' ? post.targetCompanyName ?? 'a company' : post.industryNames.join(', ')

  return (
    <span className={`news-impact num tone-${direction.tone}`}>
      <span aria-hidden="true">{direction.glyph} </span>
      {direction.sign}
      {Number(post.impactPercent ?? 0).toFixed(2)}%
      {target ? <span className="news-impact-target"> · {target}</span> : null}
    </span>
  )
}

// A crisis is only shown in the banner while it is still fresh, so an old shock does not linger at the top.
const CRISIS_RECENT_CYCLES = 15
const MAX_SECTOR_CHIPS = 6

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
          <span className="crisis-banner-title">{latest.title}</span>
        </p>
        <p className="crisis-banner-meta num">
          {sectorCount} {sectorLabel(sectorCount)} · {formatDropRange(latest)} · cycle{' '}
          {latest.triggeredInCycleNumber}
        </p>
      </div>
    </div>
  )
}

// The market effect of a crisis: always a drop, summarised as a range across the sectors it hit.
function CrisisImpact({ crisis }) {
  const sectorCount = crisis.industries.length
  return (
    <span className="news-impact num tone-down">
      <span aria-hidden="true">▼ </span>
      {formatDropRange(crisis)}
      <span className="news-impact-target">
        {' '}
        · {sectorCount} {sectorLabel(sectorCount)}
      </span>
    </span>
  )
}

function CrisisSectors({ crisis }) {
  const shown = crisis.industries.slice(0, MAX_SECTOR_CHIPS)
  const extra = crisis.industries.length - shown.length

  return (
    <ul className="crisis-sectors">
      {shown.map((link) => (
        <li key={link.industryId} className="crisis-sector num">
          {link.industryName} <span className="tone-down">−{Number(link.impactPercent).toFixed(1)}%</span>
        </li>
      ))}
      {extra > 0 ? <li className="crisis-sector crisis-sector-more">+{extra} more</li> : null}
    </ul>
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

// The market effect of a science investigation: always a rise, summarised as a range across the sectors it lifted.
function ScienceImpact({ investigation }) {
  const sectorCount = investigation.industries.length
  return (
    <span className="news-impact num tone-up">
      <span aria-hidden="true">▲ </span>
      {formatGainRange(investigation)}
      <span className="news-impact-target">
        {' '}
        · {sectorCount} {sectorLabel(sectorCount)}
      </span>
    </span>
  )
}

function ScienceSectors({ investigation }) {
  const shown = investigation.industries.slice(0, MAX_SECTOR_CHIPS)
  const extra = investigation.industries.length - shown.length

  return (
    <ul className="science-sectors">
      {shown.map((link) => (
        <li key={link.industryId} className="science-sector num">
          {link.industryName} <span className="tone-up">+{Number(link.impactPercent).toFixed(1)}%</span>
        </li>
      ))}
      {extra > 0 ? <li className="science-sector science-sector-more">+{extra} more</li> : null}
    </ul>
  )
}

// A bankruptcy carries no price impact, so the chip summarises the trader's losses: the cash wiped out and
// the value of the holdings being liquidated.
function BankruptcyImpact({ bankruptcy }) {
  return (
    <span className="news-impact num tone-down">
      <span aria-hidden="true">▼ </span>
      {formatCompactMoney(bankruptcy.cashLost)} cash
      <span className="news-impact-target">
        {' '}
        · {formatCompactMoney(bankruptcy.shareWorth)} in shares
      </span>
    </span>
  )
}

// The Newswire blends manual/automated news with crises, science investigations, and bankruptcies into one
// time-ordered feed, trimmed to the latest items; crises and bankruptcies render as red alerts and science
// breakthroughs as green ones, so market-moving events stand out from ordinary headlines.
function NewswirePanel({ news, crises, scienceInvestigations, bankruptcies, companies, onPublished }) {
  const [adding, setAdding] = useState(false)

  const feed = [
    ...news.map((post) => ({ kind: 'news', id: `news-${post.id}`, at: post.publishedAt, post })),
    ...crises.map((crisis) => ({ kind: 'crisis', id: `crisis-${crisis.id}`, at: crisis.triggeredAt, crisis })),
    ...scienceInvestigations.map((investigation) => ({
      kind: 'science',
      id: `science-${investigation.id}`,
      at: investigation.triggeredAt,
      investigation,
    })),
    ...bankruptcies.map((bankruptcy) => ({
      kind: 'bankruptcy',
      id: `bankruptcy-${bankruptcy.id}`,
      at: bankruptcy.triggeredAt,
      bankruptcy,
    })),
  ]
    .sort((a, b) => new Date(b.at) - new Date(a.at))
    .slice(0, 10)

  return (
    <Panel
      title="Newswire"
      count={`${feed.length}`}
      className="panel-news"
      headerExtra={
        <button type="button" className="btn select-sm" onClick={() => setAdding(true)}>
          + Add news
        </button>
      }
    >
      {feed.length === 0 ? (
        <p className="note">No news yet. Start the loop or add a post to see events here.</p>
      ) : (
        <ul className="newswire">
          {feed.map((item) => {
            if (item.kind === 'crisis') {
              return (
                <li key={item.id} className="news-item crisis-item">
                  <div className="news-head">
                    <h3 className="news-title">
                      <span className="crisis-flag" aria-hidden="true">
                        ⚠{' '}
                      </span>
                      {item.crisis.title}
                    </h3>
                    <CrisisImpact crisis={item.crisis} />
                  </div>
                  <p className="news-body">{item.crisis.content}</p>
                  <CrisisSectors crisis={item.crisis} />
                </li>
              )
            }

            if (item.kind === 'science') {
              return (
                <li key={item.id} className="news-item science-item">
                  <div className="news-head">
                    <h3 className="news-title">
                      <span className="science-flag" aria-hidden="true">
                        🔬{' '}
                      </span>
                      {item.investigation.title}
                    </h3>
                    <ScienceImpact investigation={item.investigation} />
                  </div>
                  <p className="news-body">{item.investigation.content}</p>
                  <ScienceSectors investigation={item.investigation} />
                </li>
              )
            }

            if (item.kind === 'bankruptcy') {
              return (
                <li key={item.id} className="news-item bankruptcy-item">
                  <div className="news-head">
                    <h3 className="news-title">
                      <span className="bankruptcy-flag" aria-hidden="true">
                        💥{' '}
                      </span>
                      {item.bankruptcy.title}
                    </h3>
                    <BankruptcyImpact bankruptcy={item.bankruptcy} />
                  </div>
                  <p className="news-body">{item.bankruptcy.content}</p>
                </li>
              )
            }

            return (
              <li key={item.id} className="news-item">
                <div className="news-head">
                  <h3 className="news-title">{item.post.title}</h3>
                  <NewsImpact post={item.post} />
                </div>
                <p className="news-body">{item.post.content}</p>
              </li>
            )
          })}
        </ul>
      )}

      {adding ? (
        <AddNewsModal companies={companies} onClose={() => setAdding(false)} onPublished={onPublished} />
      ) : null}
    </Panel>
  )
}

// Manual news composer: pick a target (one company or one/several industries), a theme for the wording, and
// the impact direction and percent. Submitting posts to the backend, which generates the headline and moves
// the affected prices.
function AddNewsModal({ companies, onClose, onPublished }) {
  const [themes, setThemes] = useState([])
  const [industries, setIndustries] = useState([])
  const [scope, setScope] = useState('Company')
  const [themeKey, setThemeKey] = useState('')
  const [direction, setDirection] = useState('Increase')
  const [impactPercent, setImpactPercent] = useState('2')
  const [companyId, setCompanyId] = useState('')
  const [industryIds, setIndustryIds] = useState([])
  const [allIndustries, setAllIndustries] = useState(false)
  const [submitting, setSubmitting] = useState(false)
  const [error, setError] = useState(null)
  const dialogRef = useRef(null)

  useEffect(() => {
    let active = true
    Promise.all([api.getNewsThemes(), api.getIndustries()])
      .then(([themeData, industryData]) => {
        if (!active) return
        setThemes(themeData)
        setIndustries(industryData)
        setThemeKey((current) => current || themeData[0]?.key || '')
      })
      .catch(() => setError('Could not load themes and industries.'))
    return () => {
      active = false
    }
  }, [])

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

  const resolvedCompanyId = companyId || companies[0]?.id || ''

  function toggleIndustry(id) {
    setIndustryIds((current) => (current.includes(id) ? current.filter((value) => value !== id) : [...current, id]))
  }

  function onBackdropClick(event) {
    if (event.target === event.currentTarget) onClose()
  }

  async function handleSubmit(event) {
    event.preventDefault()
    setError(null)

    const payload = {
      scope,
      themeKey,
      direction,
      impactPercent: Number(impactPercent),
      targetCompanyId: scope === 'Company' ? Number(resolvedCompanyId) : null,
      industryIds:
        scope === 'Industries' ? (allIndustries ? industries.map((industry) => industry.id) : industryIds) : null,
    }

    setSubmitting(true)
    try {
      await api.createNews(payload)
      onPublished()
      onClose()
    } catch (submitError) {
      setError(submitError.message)
    } finally {
      setSubmitting(false)
    }
  }

  return (
    <div className="modal-backdrop" onClick={onBackdropClick}>
      <div className="modal" role="dialog" aria-modal="true" aria-label="Add news" ref={dialogRef}>
        <header className="modal-head">
          <div className="command-id">
            <span className="command-label">Newswire</span>
            <h2 className="command-name">Add news</h2>
          </div>
        </header>

        <form className="modal-body news-form" onSubmit={handleSubmit}>
          <label className="field">
            <span>Impacts</span>
            <select className="select" value={scope} onChange={(event) => setScope(event.target.value)} autoFocus>
              <option value="Company">A single company</option>
              <option value="Industries">Industries</option>
            </select>
          </label>

          {scope === 'Company' ? (
            <div className="field">
              <span>Company</span>
              <CompanyCombobox
                companies={companies}
                value={resolvedCompanyId}
                onChange={(id) => setCompanyId(String(id))}
              />
            </div>
          ) : (
            <div className="field">
              <span>Industries</span>
              <label className="industry-check industry-check-all">
                <input
                  type="checkbox"
                  checked={allIndustries}
                  onChange={(event) => setAllIndustries(event.target.checked)}
                />
                <span>All industries</span>
              </label>
              <div className="industry-picker" aria-disabled={allIndustries}>
                {industries.map((industry) => (
                  <label key={industry.id} className="industry-check">
                    <input
                      type="checkbox"
                      disabled={allIndustries}
                      checked={allIndustries || industryIds.includes(industry.id)}
                      onChange={() => toggleIndustry(industry.id)}
                    />
                    <span>{industry.name}</span>
                  </label>
                ))}
              </div>
            </div>
          )}

          <label className="field">
            <span>Theme</span>
            <select className="select" value={themeKey} onChange={(event) => setThemeKey(event.target.value)}>
              {themes.map((theme) => (
                <option key={theme.key} value={theme.key}>
                  {theme.label}
                </option>
              ))}
            </select>
          </label>

          <div className="field-pair">
            <label className="field">
              <span>Impact</span>
              <select className="select" value={direction} onChange={(event) => setDirection(event.target.value)}>
                <option value="Increase">Increase ▲</option>
                <option value="Decrease">Decrease ▼</option>
              </select>
            </label>
            <label className="field">
              <span>Percent</span>
              <input
                className="select num"
                type="number"
                min="0.1"
                max="95"
                step="0.1"
                value={impactPercent}
                onChange={(event) => setImpactPercent(event.target.value)}
              />
            </label>
          </div>

          {error ? (
            <p className="command-error" role="alert">
              {error}
            </p>
          ) : null}

          <footer className="modal-foot">
            <button type="button" className="btn" onClick={onClose}>
              Cancel
            </button>
            <button type="submit" className="btn btn-primary" disabled={submitting}>
              Publish news
            </button>
          </footer>
        </form>
      </div>
    </div>
  )
}

function OrderBookPanel({
  orders,
  participantNameById,
  bankruptParticipantIds,
  companyNameById,
  companyPriceById,
  player,
  playerHoldingCompanyIds,
  onTraded,
}) {
  const [tradeOrder, setTradeOrder] = useState(null)
  const [activeSide, setActiveSide] = useState('Buy')
  const tabRefs = useRef({})

  // Sells list highest price first. Buys surface the companies the player already holds first (so it can add
  // to or defend those positions), then everything else, each group ordered by highest price.
  const buys = orders
    .filter((order) => order.type === 'Buy')
    .sort((a, b) => {
      const aHeld = playerHoldingCompanyIds.has(a.companyId)
      const bHeld = playerHoldingCompanyIds.has(b.companyId)
      if (aHeld !== bHeld) return aHeld ? -1 : 1
      return b.limitPrice - a.limitPrice
    })
  const sells = orders
    .filter((order) => order.type === 'Sell')
    .sort((a, b) => b.limitPrice - a.limitPrice)

  const sides = [
    { key: 'Buy', tone: 'up', orders: buys },
    { key: 'Sell', tone: 'down', orders: sells },
  ]
  const active = sides.find((side) => side.key === activeSide) ?? sides[0]

  function focusTab(key) {
    setActiveSide(key)
    tabRefs.current[key]?.focus()
  }

  function onTabKeyDown(event) {
    if (event.key !== 'ArrowRight' && event.key !== 'ArrowLeft') return
    event.preventDefault()
    focusTab(activeSide === 'Buy' ? 'Sell' : 'Buy')
  }

  return (
    <Panel title="Order book" count={`${orders.length} open`} className="panel-book">
      <div className="book-tabs tabs" role="tablist" aria-label="Order book side" onKeyDown={onTabKeyDown}>
        {sides.map((side) => {
          const selected = side.key === activeSide
          return (
            <button
              key={side.key}
              type="button"
              role="tab"
              id={`booktab-${side.key}`}
              aria-selected={selected}
              aria-controls={`bookpanel-${side.key}`}
              tabIndex={selected ? 0 : -1}
              ref={(element) => {
                tabRefs.current[side.key] = element
              }}
              className={`tab${selected ? ' is-active' : ''}`}
              onClick={() => setActiveSide(side.key)}
            >
              {side.key}
              <span className="num book-tab-count">{side.orders.length}</span>
            </button>
          )
        })}
      </div>
      <div className="tabpanel" role="tabpanel" id={`bookpanel-${active.key}`} aria-labelledby={`booktab-${active.key}`}>
        <OrderSide
          side={active.key}
          tone={active.tone}
          orders={active.orders}
          participantNameById={participantNameById}
          bankruptParticipantIds={bankruptParticipantIds}
          companyNameById={companyNameById}
          companyPriceById={companyPriceById}
          player={player}
          playerHoldingCompanyIds={playerHoldingCompanyIds}
          onTrade={setTradeOrder}
        />
      </div>
      {tradeOrder ? (
        <TradeOrderModal
          key={tradeOrder.id}
          order={tradeOrder}
          player={player}
          companyName={companyNameById.get(tradeOrder.companyId) ?? `#${tradeOrder.companyId}`}
          currentPrice={companyPriceById.get(tradeOrder.companyId) ?? null}
          onClose={() => setTradeOrder(null)}
          onTraded={onTraded}
        />
      ) : null}
    </Panel>
  )
}

function OrderSide({ side, tone, orders, participantNameById, bankruptParticipantIds, companyNameById, companyPriceById, player, playerHoldingCompanyIds, onTrade }) {
  if (orders.length === 0) {
    return <p className="note note-sm">No {side.toLowerCase()} orders.</p>
  }

  return (
    <div className="tbl-scroll">
      <table className="tbl tbl-book">
        <thead>
          <tr>
            <th scope="col" className="ta-r">
              Order price
            </th>
            <th scope="col" className="ta-r">
              Market price
            </th>
            <th scope="col" className="ta-r">
              Quantity
            </th>
            <th scope="col">Trader</th>
          </tr>
        </thead>
        <tbody>
          {orders.map((order) => {
            const isOwn = player != null && order.participantId === player.id
            const actionable = player != null && !isOwn
            const isBankrupt = !!bankruptParticipantIds?.has(order.participantId)
            const remaining = order.quantity - order.filledQuantity
            const companyName = companyNameById.get(order.companyId) ?? `#${order.companyId}`
            const marketPrice = companyPriceById.get(order.companyId) ?? null
            // How far the order's limit sits from the live market price, so the gap reads at a glance.
            const percentDiff =
              marketPrice != null && marketPrice > 0 ? ((order.limitPrice - marketPrice) / marketPrice) * 100 : null
            const diffLabel =
              percentDiff != null
                ? `${percentDiff > 0 ? '+' : percentDiff < 0 ? '−' : ''}${Math.abs(percentDiff).toFixed(1)}%`
                : null
            const diffGlyph = percentDiff == null ? '' : percentDiff > 0 ? '▲' : percentDiff < 0 ? '▼' : '◆'
            const diffTone = percentDiff == null || percentDiff === 0 ? 'flat' : percentDiff > 0 ? 'up' : 'down'
            // A bid the player can sell into: they hold shares of its company and it is not their own order.
            const sellable = side === 'Buy' && actionable && !!playerHoldingCompanyIds?.has(order.companyId)
            // The player takes the opposite side: buy a resting sell offer, sell into a resting buy order.
            const actionLabel =
              side === 'Sell'
                ? `Buy ${remaining} ${companyName} shares at ${formatMoney(order.limitPrice)}`
                : `Sell ${remaining} ${companyName} shares at ${formatMoney(order.limitPrice)}`
            const rowClass = `book-row${actionable ? ' is-actionable' : ''}${isOwn ? ' is-own' : ''}${sellable ? ' is-sellable' : ''}`
            const handlers = actionable
              ? {
                  role: 'button',
                  tabIndex: 0,
                  'aria-label': actionLabel,
                  title: side === 'Sell' ? 'Buy this offer' : 'Sell into this bid',
                  onClick: () => onTrade(order),
                  onKeyDown: (event) => {
                    if (event.key === 'Enter' || event.key === ' ') {
                      event.preventDefault()
                      onTrade(order)
                    }
                  },
                }
              : {}
            return (
              <tr key={order.id} className={rowClass} {...handlers}>
                <td className={`num ta-r tone-${tone}`}>
                  {formatMoney(order.limitPrice)}
                  {diffLabel ? (
                    <span className={`book-diff num tone-${diffTone}`} title="Order price versus current market price">
                      <span aria-hidden="true">{diffGlyph} </span>
                      {diffLabel}
                    </span>
                  ) : null}
                </td>
                <td className="num ta-r">{marketPrice != null ? formatMoney(marketPrice) : '—'}</td>
                <td className="num ta-r">
                  {remaining}
                  <span className="muted-sub">/{order.quantity}</span>
                </td>
                <td>
                  <span className="cell-trader">
                    <span className="cell-ellipsis">
                      {sellable ? (
                        <span className="book-hold" title="You hold shares to sell into this bid" aria-hidden="true">
                          ●{' '}
                        </span>
                      ) : null}
                      {isOwn ? 'You' : traderName(order.participantId, participantNameById)}
                      <span className="muted-sub"> · {companyName}</span>
                    </span>
                    {side === 'Sell' && isBankrupt ? <span className="tag tag-bankrupt">Bankrupt</span> : null}
                  </span>
                </td>
              </tr>
            )
          })}
        </tbody>
      </table>
    </div>
  )
}

// Confirms a player trade against one resting order. The player takes the opposite side at that order's
// limit; like every order it settles on the next cycle at the midpoint, so this books a competitive order
// rather than instantly hitting the shown counterparty.
function TradeOrderModal({ order, player, companyName, currentPrice, onClose, onTraded }) {
  const takingSellOffer = order.type === 'Sell'
  const playerSide = takingSellOffer ? 'Buy' : 'Sell'
  const remaining = order.quantity - order.filledQuantity
  // Selling into a bid needs the player's holding for this company; buying an offer does not (null = pending).
  const [ownedShares, setOwnedShares] = useState(takingSellOffer ? 0 : null)
  const [quantity, setQuantity] = useState(takingSellOffer ? String(remaining) : '')
  const [submitting, setSubmitting] = useState(false)
  const [error, setError] = useState(null)

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

  // A sell can only offer shares the player holds, so seed the quantity with the most that both the holding
  // and the resting bid can absorb.
  useEffect(() => {
    if (takingSellOffer) return undefined

    let active = true
    api
      .getHoldings(player.id)
      .then((holdings) => {
        if (!active) return
        const holding = holdings.find((item) => item.companyId === order.companyId)
        const owned = holding ? holding.shares : 0
        setOwnedShares(owned)
        setQuantity(owned > 0 ? String(Math.min(owned, remaining)) : '')
      })
      .catch(() => {
        if (active) setOwnedShares(0)
      })
    return () => {
      active = false
    }
  }, [takingSellOffer, player.id, order.companyId, remaining])

  function onBackdropClick(event) {
    if (event.target === event.currentTarget) onClose()
  }

  const loadingHoldings = !takingSellOffer && ownedShares === null
  const noShares = !takingSellOffer && ownedShares === 0
  const maxQuantity = takingSellOffer ? remaining : Math.min(ownedShares ?? 0, remaining)
  const quantityNumber = Number(quantity)
  const validQuantity = Number.isInteger(quantityNumber) && quantityNumber > 0 && quantityNumber <= maxQuantity
  const total = (validQuantity ? quantityNumber : 0) * order.limitPrice

  // How far the order's limit sits from the live market price, so the player can judge the deal at a glance.
  const percentDiff =
    currentPrice != null && currentPrice > 0 ? ((order.limitPrice - currentPrice) / currentPrice) * 100 : null
  const diffLabel =
    percentDiff != null
      ? `${percentDiff > 0 ? '+' : percentDiff < 0 ? '−' : ''}${Math.abs(percentDiff).toFixed(1)}%`
      : null
  const diffGlyph = percentDiff == null ? '' : percentDiff > 0 ? '▲' : percentDiff < 0 ? '▼' : '◆'
  // The deal favors the player when they buy below market or sell above it, which drives the badge colour.
  const favorable =
    percentDiff == null || percentDiff === 0 ? null : takingSellOffer ? percentDiff < 0 : percentDiff > 0
  const diffTone = favorable == null ? 'flat' : favorable ? 'up' : 'down'

  async function handleSubmit(event) {
    event.preventDefault()
    setError(null)
    setSubmitting(true)
    try {
      await api.placeOrder({
        participantId: player.id,
        companyId: order.companyId,
        type: playerSide,
        quantity: quantityNumber,
        limitPrice: order.limitPrice,
      })
      await onTraded()
      onClose()
    } catch (submitError) {
      setError(submitError.message)
    } finally {
      setSubmitting(false)
    }
  }

  return (
    <div className="modal-backdrop" onClick={onBackdropClick}>
      <div className="modal" role="dialog" aria-modal="true" aria-label="Trade against order">
        <header className="modal-head">
          <div className="command-id">
            <span className="command-label">Order book</span>
            <h2 className="command-name">{takingSellOffer ? 'Buy this offer' : 'Sell into this bid'}</h2>
          </div>
        </header>

        <form className="modal-body" onSubmit={handleSubmit}>
          <div className="trade-quote">
            <span className="map-stat-label">Order price</span>
            <div className="quote">
              <span className="quote-last num">{formatMoney(order.limitPrice)}</span>
              {diffLabel ? (
                <span className={`quote-change num tone-${diffTone}`} title="Order price versus current market price">
                  <span aria-hidden="true">{diffGlyph} </span>
                  {diffLabel} vs market
                </span>
              ) : null}
            </div>
          </div>

          <dl className="modal-stats">
            <div>
              <dt>Company</dt>
              <dd>{companyName}</dd>
            </div>
            <div>
              <dt>You</dt>
              <dd className={`tone-${takingSellOffer ? 'up' : 'down'}`}>
                <span aria-hidden="true">{takingSellOffer ? '▲' : '▼'} </span>
                {playerSide}
              </dd>
            </div>
            <div>
              <dt>Market price</dt>
              <dd className="num">{currentPrice != null ? formatMoney(currentPrice) : '—'}</dd>
            </div>
            <div>
              <dt>Total</dt>
              <dd className="num">{formatMoney(total)}</dd>
            </div>
          </dl>

          {loadingHoldings ? (
            <p className="note note-sm">Checking your holdings…</p>
          ) : noShares ? (
            <p className="note note-sm">You hold no {companyName} shares to sell.</p>
          ) : (
            <>
              <label className="field">
                <span>Quantity (max {maxQuantity})</span>
                <input
                  className="select num"
                  type="number"
                  min="1"
                  max={maxQuantity}
                  step="1"
                  value={quantity}
                  onChange={(event) => setQuantity(event.target.value)}
                  autoFocus
                />
              </label>

              <p className="note note-sm">
                Places a matching {playerSide.toLowerCase()} order that fills on the next cycle at the midpoint price.
              </p>
            </>
          )}

          {error ? (
            <p className="command-error" role="alert">
              {error}
            </p>
          ) : null}

          <footer className="modal-foot">
            <button type="button" className="btn" onClick={onClose}>
              {noShares ? 'Close' : 'Cancel'}
            </button>
            {loadingHoldings || noShares ? null : (
              <button type="submit" className="btn btn-primary" disabled={submitting || !validQuantity}>
                {submitting ? 'Placing…' : `Place ${playerSide.toLowerCase()} order`}
              </button>
            )}
          </footer>
        </form>
      </div>
    </div>
  )
}

// Layout box for the treemap; tile positions are emitted as percentages of it, and the panel keeps this
// aspect ratio so the proportions hold at any width.
const MAP_BOX_W = 100
const MAP_BOX_H = 42
const TONE_GLYPH = { up: '▲', down: '▼', flat: '–' }
const TONE_WORD = { up: 'up', down: 'down', flat: 'unchanged' }

function formatPct(value) {
  if (typeof value !== 'number') return '—'
  const sign = value > 0 ? '+' : value < 0 ? '−' : ''
  return `${sign}${(Math.abs(value) * 100).toFixed(1)}%`
}

function heatMix(value) {
  if (typeof value !== 'number' || value === 0) return '0%'
  return `${Math.min(88, 44 + Math.abs(value) * 900).toFixed(0)}%`
}

function mapTileSize(areaPct, widthPct, heightPct) {
  const shortestSide = Math.min(widthPct, heightPct)
  if (areaPct < 1.1 || shortestSide < 8) return 'is-tiny'
  if (areaPct < 2.2 || shortestSide < 13) return 'is-small'
  return ''
}

// Worst (largest) aspect ratio a row of tile areas would reach if laid along a side of the given length.
function worstRatio(areas, side, sum) {
  if (areas.length === 0 || sum <= 0) return Infinity
  const max = Math.max(...areas)
  const min = Math.min(...areas)
  const side2 = side * side
  const sum2 = sum * sum
  return Math.max((side2 * max) / sum2, sum2 / (side2 * min))
}

// Squarified treemap (Bruls, Huizing, van Wijk): packs items into the box with area proportional to
// value, growing each row only while it keeps tiles close to square. Items must be sorted largest first.
function squarify(items, width, height) {
  const total = items.reduce((sum, item) => sum + item.value, 0)
  if (total <= 0) return []

  const scale = (width * height) / total
  const nodes = items.map((item) => ({ item, area: item.value * scale }))

  const placed = []
  let free = { x: 0, y: 0, w: width, h: height }
  let index = 0

  while (index < nodes.length) {
    const side = Math.min(free.w, free.h)
    const row = []
    let rowSum = 0

    while (index + row.length < nodes.length) {
      const next = nodes[index + row.length]
      const current = row.map((node) => node.area)
      const widened = [...current, next.area]
      if (row.length === 0 || worstRatio(widened, side, rowSum + next.area) <= worstRatio(current, side, rowSum)) {
        row.push(next)
        rowSum += next.area
      } else {
        break
      }
    }

    const thickness = rowSum / side
    if (free.w >= free.h) {
      let y = free.y
      for (const node of row) {
        const cellHeight = node.area / thickness
        placed.push({ ...node.item, x: free.x, y, w: thickness, h: cellHeight })
        y += cellHeight
      }
      free = { x: free.x + thickness, y: free.y, w: free.w - thickness, h: free.h }
    } else {
      let x = free.x
      for (const node of row) {
        const cellWidth = node.area / thickness
        placed.push({ ...node.item, x, y: free.y, w: cellWidth, h: thickness })
        x += cellWidth
      }
      free = { x: free.x, y: free.y + thickness, w: free.w, h: free.h - thickness }
    }

    index += row.length
  }

  return placed
}

// Treemap of the largest companies by capitalisation: tile area tracks market cap, colour tracks the change
// in capitalisation (green up, red down, grey flat) with a glyph and signed percent so it is never colour-only.
// The latest market headline, pinned above the treemap. It stays put until a newer post arrives (the feed is
// newest-first, so this is always news[0]) and ages by cycle. Impact direction reads as a glyph plus the signed
// percent, never colour alone; with no news yet it still renders with a hint.
function LatestNewsStrip({ news, currentCycleNumber }) {
  if (!news) {
    return (
      <div className="map-news map-news-empty">
        <span className="map-news-label">Latest news</span>
        <span className="map-news-hint">No market news yet.</span>
      </div>
    )
  }

  const cyclesAgo = Math.max(0, currentCycleNumber - news.publishedInCycleNumber)
  const ageLabel = cyclesAgo === 0 ? 'this cycle' : `${cyclesAgo} cycle${cyclesAgo === 1 ? '' : 's'} ago`
  const tone = news.direction === 'Increase' ? 'up' : news.direction === 'Decrease' ? 'down' : null
  const hasImpact = tone && typeof news.impactPercent === 'number'

  return (
    <div className="map-news">
      <div className="map-news-head">
        <span className="map-news-label">Latest news</span>
        {hasImpact ? (
          <span className={`map-news-impact num tone-${tone}`}>
            <span aria-hidden="true">{tone === 'up' ? '▲' : '▼'} </span>
            {tone === 'up' ? '+' : '−'}
            {Math.abs(news.impactPercent).toFixed(0)}%
          </span>
        ) : null}
        <span className="map-news-age num">{ageLabel}</span>
      </div>
      <p className="map-news-title">{news.title}</p>
      <p className="map-news-body">{news.content}</p>
    </div>
  )
}

function MarketMapPanel({ companies, participants, lastDividendTotal, currentCycleNumber, latestNews, onSelectCompany }) {
  // Tile colour tracks the change in a company's total capitalisation, not its per-share price, so a stock
  // split (shares up, price down, capitalisation unchanged) reads as flat rather than a market-wide crash.
  // Anchored to the cycle number, not the poll: the move is measured against the previous cycle's caps and the
  // colour holds through every poll of the current cycle, only re-computing when a new cycle advances.
  const [capChange, setCapChange] = useState({ cycle: null, capById: new Map(), changeById: new Map() })
  if (capChange.cycle !== currentCycleNumber) {
    const previousCaps = capChange.capById
    const capById = new Map()
    const changeById = new Map()
    for (const company of companies) {
      const cap = company.issuedSharesCount * (company.currentPrice ?? 0)
      capById.set(company.id, cap)
      const previousCap = previousCaps.get(company.id)
      changeById.set(company.id, previousCap > 0 ? (cap - previousCap) / previousCap : 0)
    }
    setCapChange({ cycle: currentCycleNumber, capById, changeById })
  }
  const capChangeById = capChange.changeById

  const mappedCompanies = companies
    .map((company) => ({
      ...company,
      capitalization: company.issuedSharesCount * (company.currentPrice ?? 0),
      capChangePct: capChangeById.get(company.id) ?? 0,
    }))
    .filter((company) => company.capitalization > 0)
    .sort((a, b) => b.capitalization - a.capitalization)
  const totalShares = mappedCompanies.reduce((sum, company) => sum + company.issuedSharesCount, 0)

  // Capitalisation values every issued share at its company's current price, matching the tile areas;
  // participant cash is the cash side of the same market.
  const totalCapitalization = mappedCompanies.reduce((sum, company) => sum + company.capitalization, 0)
  const totalParticipantMoney = participants.reduce(
    (sum, participant) => sum + (participant.currentBalance ?? 0),
    0,
  )

  const tiles = squarify(
    mappedCompanies.map((company) => ({ company, value: company.capitalization })),
    MAP_BOX_W,
    MAP_BOX_H,
  )

  return (
    <Panel
      title="Market map"
      count={mappedCompanies.length ? `${mappedCompanies.length} companies · ${formatInt(totalShares)} shares` : undefined}
      className="panel-map"
    >
      <LatestNewsStrip news={latestNews} currentCycleNumber={currentCycleNumber} />
      {mappedCompanies.length === 0 ? (
        <p className="note">Seed the market to see company prices.</p>
      ) : (
        <div className="map-layout">
        <div className="market-map" style={{ aspectRatio: `${MAP_BOX_W} / ${MAP_BOX_H}` }}>
          {tiles.map(({ company, x, y, w, h }) => {
            const tone = toneOf(company.capChangePct)
            const widthPct = (w / MAP_BOX_W) * 100
            const heightPct = (h / MAP_BOX_H) * 100
            const areaPct = (company.capitalization / totalCapitalization) * 100
            const sizeClass = mapTileSize(areaPct, widthPct, heightPct)
            return (
              <div
                key={company.id}
                className={`map-tile tone-bg-${tone} ${sizeClass}`}
                role="button"
                tabIndex={0}
                onClick={() => onSelectCompany(company.id)}
                onKeyDown={(event) => {
                  if (event.key === 'Enter' || event.key === ' ') {
                    event.preventDefault()
                    onSelectCompany(company.id)
                  }
                }}
                style={{
                  left: `${(x / MAP_BOX_W) * 100}%`,
                  top: `${(y / MAP_BOX_H) * 100}%`,
                  width: `${widthPct}%`,
                  height: `${heightPct}%`,
                  '--map-area': areaPct.toFixed(2),
                  '--map-heat': heatMix(company.capChangePct),
                }}
                title={`${company.name} · ${formatCompactMoney(company.capitalization)} cap · ${formatInt(company.issuedSharesCount)} shares · ${formatMoney(company.currentPrice)} · ${formatPct(company.capChangePct)}`}
                aria-label={`${company.name}, ${formatCompactMoney(company.capitalization)} capitalisation, ${formatInt(company.issuedSharesCount)} issued shares, ${formatMoney(company.currentPrice)}, ${TONE_WORD[tone]} ${formatPct(company.capChangePct)}. Open details.`}
              >
                <span className="map-name">{company.name}</span>
                <span className="map-cap num">{formatCompactMoney(company.capitalization)}</span>
                <span className="map-change num">
                  <span aria-hidden="true">{TONE_GLYPH[tone]}</span> {formatPct(company.capChangePct)}
                </span>
              </div>
            )
          })}
        </div>
        <aside className="map-stats">
          <div className="map-stat">
            <span className="map-stat-label">Total cap</span>
            <span className="map-stat-value num" title={formatMoney(totalCapitalization)}>
              {formatCompactMoney(totalCapitalization)}
            </span>
          </div>
          <div className="map-stat">
            <span className="map-stat-label">Trader cash</span>
            <span className="map-stat-value num" title={formatMoney(totalParticipantMoney)}>
              {formatCompactMoney(totalParticipantMoney)}
            </span>
          </div>
          <div className="map-stat">
            <span className="map-stat-label">Last dividends</span>
            <span className="map-stat-value num" title={formatMoney(lastDividendTotal)}>
              {formatCompactMoney(lastDividendTotal)}
            </span>
          </div>
        </aside>
        </div>
      )}
    </Panel>
  )
}

const ACTIVITY_WINDOW = 48

function ActivityBody({ activity }) {
  // The first cycle can hold a large backlog of orders, so the chart shows a recent window to keep
  // the scale readable; the total stays all-time.
  const points = activity.slice(-ACTIVITY_WINDOW)
  const total = activity.reduce((sum, point) => sum + point.ordersPlaced, 0)
  const windowCounts = points.map((point) => point.ordersPlaced)
  const peak = windowCounts.length ? Math.max(...windowCounts) : 0
  const hasDividend = points.some((point) => point.paidDividend)

  if (points.length < 2) {
    return <p className="note">Start the loop or step a cycle to see orders placed per loop.</p>
  }

  return (
    <>
      <p className="tabpanel-meta">{formatInt(total)} orders</p>
      <div className="quote">
        <span className="muted-sub">Peak {formatInt(peak)} in last {points.length}</span>
        {hasDividend ? <span className="activity-legend">dividend cycle</span> : null}
      </div>
      <ActivityChart points={points} />
    </>
  )
}

// Line-and-area chart of orders placed per loop, with a labelled count axis (Y) and cycle axis (X).
function ActivityChart({ points }) {
  const width = 720
  const height = 96
  const margin = { top: 10, right: 12, bottom: 24, left: 44 }
  const plotWidth = width - margin.left - margin.right
  const plotHeight = height - margin.top - margin.bottom

  const counts = points.map((point) => point.ordersPlaced)
  const tickCount = 3
  const step = Math.max(1, Math.ceil(Math.max(...counts) / tickCount))
  const yMax = step * tickCount
  const yTicks = Array.from({ length: tickCount + 1 }, (_, index) => index * step)

  const count = points.length
  const x = (index) => margin.left + (count === 1 ? plotWidth / 2 : (index * plotWidth) / (count - 1))
  const y = (value) => margin.top + plotHeight - (value / yMax) * plotHeight
  const baseline = margin.top + plotHeight

  const line = points.map((point, index) => `${x(index)},${y(point.ordersPlaced)}`).join(' ')
  const area = `${x(0)},${baseline} ${line} ${x(count - 1)},${baseline}`
  const last = points.at(-1)

  const indexed = points.map((point, index) => ({ point, index }))
  // Label even cycle numbers only so they never overlap, always keeping the most recent one.
  const xLabels = indexed.filter(({ point, index }) => point.cycleNumber % 2 === 0 || index === count - 1)
  const dividendLines = indexed.filter(({ point }) => point.paidDividend)

  return (
    <div className="activity-chart">
      <svg
        viewBox={`0 0 ${width} ${height}`}
        role="img"
        aria-label={
          `Orders placed per loop across the last ${count} cycles, peaking at ${Math.max(...counts)}.` +
          (dividendLines.length
            ? ` Dividends were paid in cycle${dividendLines.length > 1 ? 's' : ''} ${dividendLines.map(({ point }) => point.cycleNumber).join(', ')}.`
            : '')
        }
      >
        <defs>
          <linearGradient id="activity-fill" x1="0" x2="0" y1="0" y2="1">
            <stop offset="0%" stopColor="currentColor" stopOpacity="0.18" />
            <stop offset="100%" stopColor="currentColor" stopOpacity="0" />
          </linearGradient>
        </defs>
        <g className="chart-axis" aria-hidden="true">
          {yTicks.map((tick) => (
            <g key={tick}>
              <line className="chart-gridline" x1={margin.left} x2={width - margin.right} y1={y(tick)} y2={y(tick)} />
              <text className="chart-tick chart-tick-y" x={margin.left - 8} y={y(tick)}>
                {tick}
              </text>
            </g>
          ))}
          {indexed.map(({ index }) => (
            <line
              key={`v-${index}`}
              className="chart-gridline"
              x1={x(index)}
              x2={x(index)}
              y1={margin.top}
              y2={baseline}
            />
          ))}
          {xLabels.map(({ point, index }) => (
            <text key={point.cycleNumber} className="chart-tick chart-tick-x" x={x(index)} y={height - 8}>
              {point.cycleNumber}
            </text>
          ))}
        </g>
        <polygon className="activity-area" points={area} fill="url(#activity-fill)" />
        <polyline className="activity-line" points={line} />
        {/* Dashed so the dividend marker reads without relying on colour alone. */}
        {dividendLines.map(({ index }) => (
          <line
            key={`div-${index}`}
            className="chart-dividend-line"
            x1={x(index)}
            x2={x(index)}
            y1={margin.top}
            y2={baseline}
          />
        ))}
        {last ? <circle className="activity-dot" cx={x(count - 1)} cy={y(last.ordersPlaced)} r="3.5" /> : null}
      </svg>
    </div>
  )
}

function TradeTapeTable({ transactions, participantNameById, companyNameById }) {
  const rows = transactions.slice(0, 14)
  if (transactions.length === 0) {
    return <p className="note">No trades yet.</p>
  }
  return (
    <>
      <p className="tabpanel-meta">{transactions.length} settled</p>
      <div className="tbl-scroll">
        <table className="tbl">
            <thead>
              <tr>
                <th scope="col">Company</th>
                <th scope="col">Flow</th>
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
              {rows.map((transaction) => (
                <tr key={transaction.id}>
                  <th scope="row" className="cell-ellipsis">
                    {companyNameById.get(transaction.companyId) ?? `#${transaction.companyId}`}
                  </th>
                  <td className="cell-flow cell-ellipsis">
                    {traderName(transaction.sellerId, participantNameById)}
                    <span className="flow-arrow" aria-label="to">
                      →
                    </span>
                    {traderName(transaction.buyerId, participantNameById)}
                  </td>
                  <td className="num ta-r">{formatInt(transaction.quantity)}</td>
                  <td className="num ta-r">{formatMoney(transaction.price)}</td>
                  <td className="num ta-r">{formatMoney(transaction.totalCost)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
    </>
  )
}

export default App
