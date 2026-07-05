import { Link, useParams } from 'react-router-dom'
import './App.css'
import { CompanyDetail } from './CompanyDetail'

// Full-page company detail, reached from the Companies roster, the market map / company modal, and the
// industries views. The detail block (price chart, ownership, ratings, related news) owns its own polling
// keyed on the route id.
function CompanyDetailPage() {
  const { id } = useParams()
  const companyId = Number(id)

  return (
    <div className="app">
      <header className="topbar">
        <Link className="brand" to="/" aria-label="Back to the Trader AI dashboard">
          <span className="brand-mark" aria-hidden="true">
            TA
          </span>
          <span className="brand-name">Trader&nbsp;AI</span>
          <span className="brand-tag" aria-hidden="true">
            Company
          </span>
        </Link>
        <Link className="btn" to="/companies">
          ← Companies
        </Link>
      </header>

      <main className="main">
        <CompanyDetail key={companyId} companyId={companyId} />
      </main>
    </div>
  )
}

export default CompanyDetailPage
