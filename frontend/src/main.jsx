import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { BrowserRouter, Navigate, Route, Routes } from 'react-router-dom'
import './index.css'
import App from './App.jsx'
import TradersPage from './TradersPage.jsx'
import CompaniesPage from './CompaniesPage.jsx'
import { AppShell } from './AppShell.jsx'
import { SettingsPage } from './SettingsPage.jsx'
import { AboutPage } from './AboutPage.jsx'
import AuditorsPage from './AuditorsPage.jsx'
import AuditorDetailPage from './AuditorDetailPage.jsx'
import NewsPage from './NewsPage.jsx'
import TraderDetailPage from './TraderDetailPage.jsx'
import CompanyDetailPage from './CompanyDetailPage.jsx'
import IndustriesPage from './IndustriesPage.jsx'
import IndustryDetailPage from './IndustryDetailPage.jsx'
import TradeMarketPage from './TradeMarketPage.jsx'
import CrisesPage from './CrisesPage.jsx'
import CrisisDetailPage from './CrisisDetailPage.jsx'
import BanksPage from './BanksPage.jsx'
import BankLoansPage from './BankLoansPage.jsx'
import PlayerStatsPage from './PlayerStatsPage.jsx'
import FundStatsPage from './FundStatsPage.jsx'

createRoot(document.getElementById('root')).render(
  <StrictMode>
    <BrowserRouter>
      <Routes>
        <Route element={<AppShell />}>
          <Route path="/" element={<App />} />
          <Route path="/trade-market" element={<TradeMarketPage />} />
          <Route path="/player-stats" element={<PlayerStatsPage />} />
          <Route path="/fund-stats" element={<FundStatsPage />} />
          <Route path="/traders" element={<TradersPage />} />
          <Route path="/traders/:id" element={<TraderDetailPage />} />
          <Route path="/companies" element={<CompaniesPage />} />
          <Route path="/companies/:id" element={<CompanyDetailPage />} />
          <Route path="/closed-companies" element={<Navigate replace to="/companies?view=closed" />} />
          <Route path="/industries" element={<IndustriesPage />} />
          <Route path="/industries/:id" element={<IndustryDetailPage />} />
          <Route path="/news" element={<NewsPage />} />
          <Route path="/crises" element={<CrisesPage />} />
          <Route path="/crises/:id" element={<CrisisDetailPage />} />
          <Route path="/auditors" element={<AuditorsPage />} />
          <Route path="/auditors/:id" element={<AuditorDetailPage />} />
          <Route path="/banks" element={<BanksPage />} />
          <Route path="/loans" element={<BankLoansPage />} />
          <Route path="/settings" element={<SettingsPage />} />
          <Route path="/about" element={<AboutPage />} />
          <Route path="/departed-traders" element={<Navigate replace to="/traders?view=departed" />} />
          <Route path="/closed-funds" element={<Navigate replace to="/traders?view=closed-funds" />} />
        </Route>
      </Routes>
    </BrowserRouter>
  </StrictMode>,
)
