import { useParams } from 'react-router-dom'
import './App.css'
import { CompanyDetail } from './CompanyDetail'

// Full-page company detail, reached from the Companies roster, the market map / company modal, and the
// industries views. The detail block (price chart, ownership, ratings, related news) owns its own polling
// keyed on the route id.
function CompanyDetailPage() {
  const { id } = useParams()
  const companyId = Number(id)

  return (
    <main className="main">
      <CompanyDetail key={companyId} companyId={companyId} />
    </main>
  )
}

export default CompanyDetailPage
