import { useParams } from 'react-router-dom'
import './App.css'
import { ParticipantDetail } from './ParticipantDetail'

// Full-page trader detail, reached from the Traders roster and from the dashboard summary modal. The detail
// block (identity, worth history, holdings, orders) owns its own polling keyed on the route id.
function TraderDetailPage() {
  const { id } = useParams()
  const participantId = Number(id)

  return (
    <main className="main">
      <ParticipantDetail key={participantId} participantId={participantId} />
    </main>
  )
}

export default TraderDetailPage
