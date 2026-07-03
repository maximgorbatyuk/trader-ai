import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { BrowserRouter, Route, Routes } from 'react-router-dom'
import './index.css'
import App from './App.jsx'
import TradersPage from './TradersPage.jsx'
import CompaniesPage from './CompaniesPage.jsx'
import { AppShell } from './AppShell.jsx'
import DepartedTradersPage from './DepartedTradersPage.jsx'

createRoot(document.getElementById('root')).render(
  <StrictMode>
    <BrowserRouter>
      <Routes>
        <Route element={<AppShell />}>
          <Route path="/" element={<App />} />
          <Route path="/traders" element={<TradersPage />} />
          <Route path="/companies" element={<CompaniesPage />} />
          <Route path="/departed-traders" element={<DepartedTradersPage />} />
        </Route>
      </Routes>
    </BrowserRouter>
  </StrictMode>,
)
