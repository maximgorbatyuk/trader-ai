import { NavLink, Outlet } from 'react-router-dom'
import './App.css'

// Layout route: the persistent left sidebar plus the routed page. Every page nests under it so the sidebar
// stays put while the content area swaps; each page still owns its own topbar and polling.
export function AppShell() {
  return (
    <div className="app-shell">
      <aside className="sidebar">
        <nav className="sidebar-nav" aria-label="Primary">
          <NavLink className={({ isActive }) => `side-link${isActive ? ' is-active' : ''}`} to="/" end>
            Dashboard
          </NavLink>
          <NavLink className={({ isActive }) => `side-link${isActive ? ' is-active' : ''}`} to="/departed-traders">
            Departed traders
          </NavLink>
        </nav>
      </aside>
      <Outlet />
    </div>
  )
}
