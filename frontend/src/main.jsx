import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { BrowserRouter, Route, Routes } from 'react-router-dom'
import './index.css'
import App from './App.jsx'
import TradersPage from './TradersPage.jsx'
import CompaniesPage from './CompaniesPage.jsx'
import { AppShell } from './AppShell.jsx'
import DepartedTradersPage from './DepartedTradersPage.jsx'
import ClosedFundsPage from './ClosedFundsPage.jsx'
import ClosedCompaniesPage from './ClosedCompaniesPage.jsx'
import AuditorsPage from './AuditorsPage.jsx'
import NewsPage from './NewsPage.jsx'
import TraderDetailPage from './TraderDetailPage.jsx'
import CompanyDetailPage from './CompanyDetailPage.jsx'
import IndustriesPage from './IndustriesPage.jsx'
import TradeMarketPage from './TradeMarketPage.jsx'

createRoot(document.getElementById('root')).render(
  <StrictMode>
    <BrowserRouter>
      <Routes>
        <Route element={<AppShell />}>
          <Route path="/" element={<App />} />
          <Route path="/trade-market" element={<TradeMarketPage />} />
          <Route path="/traders" element={<TradersPage />} />
          <Route path="/traders/:id" element={<TraderDetailPage />} />
          <Route path="/companies" element={<CompaniesPage />} />
          <Route path="/companies/:id" element={<CompanyDetailPage />} />
          <Route path="/closed-companies" element={<ClosedCompaniesPage />} />
          <Route path="/industries" element={<IndustriesPage />} />
          <Route path="/news" element={<NewsPage />} />
          <Route path="/auditors" element={<AuditorsPage />} />
          <Route path="/departed-traders" element={<DepartedTradersPage />} />
          <Route path="/closed-funds" element={<ClosedFundsPage />} />
        </Route>
      </Routes>
    </BrowserRouter>
  </StrictMode>,
)
