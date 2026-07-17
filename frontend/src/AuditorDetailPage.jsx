import { useParams } from 'react-router-dom'
import './App.css'
import { AuditorDetail } from './AuditorDetail'

// Full-page auditor detail, reached from the Auditors roster. The detail block (identity and paginated audit
// history) owns its own polling keyed on the route id.
function AuditorDetailPage() {
  const { id } = useParams()
  const auditorId = Number(id)

  return (
    <main className="main">
      <AuditorDetail key={auditorId} auditorId={auditorId} />
    </main>
  )
}

export default AuditorDetailPage
