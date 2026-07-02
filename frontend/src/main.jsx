import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { BrowserRouter, Route, Routes } from 'react-router-dom'
import './index.css'
import App from './App.jsx'
import ParticipantPage from './ParticipantPage.jsx'
import CompanyPage from './CompanyPage.jsx'
import { AppShell } from './AppShell.jsx'
import DepartedTradersPage from './DepartedTradersPage.jsx'

createRoot(document.getElementById('root')).render(
  <StrictMode>
    <BrowserRouter>
      <Routes>
        <Route element={<AppShell />}>
          <Route path="/" element={<App />} />
          <Route path="/participants/:id" element={<ParticipantPage />} />
          <Route path="/companies/:id" element={<CompanyPage />} />
          <Route path="/departed-traders" element={<DepartedTradersPage />} />
        </Route>
      </Routes>
    </BrowserRouter>
  </StrictMode>,
)
