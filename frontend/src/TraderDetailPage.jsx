import { Link, useParams } from 'react-router-dom'
import './App.css'
import { ParticipantDetail } from './ParticipantDetail'

// Full-page trader detail, reached from the Traders roster and from the dashboard summary modal. The detail
// block (identity, worth history, holdings, orders) owns its own polling keyed on the route id.
function TraderDetailPage() {
  const { id } = useParams()
  const participantId = Number(id)

  return (
    <div className="app">
      <header className="topbar">
        <Link className="brand" to="/" aria-label="Back to the Trader AI dashboard">
          <span className="brand-mark" aria-hidden="true">
            TA
          </span>
          <span className="brand-name">Trader&nbsp;AI</span>
          <span className="brand-tag" aria-hidden="true">
            Trader
          </span>
        </Link>
        <Link className="btn" to="/traders">
          ← Traders
        </Link>
      </header>

      <main className="main">
        <ParticipantDetail key={participantId} participantId={participantId} />
      </main>
    </div>
  )
}

export default TraderDetailPage
