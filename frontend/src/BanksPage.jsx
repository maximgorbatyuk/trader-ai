import { useCallback, useEffect, useState } from 'react'
import './App.css'
import { api } from './api'
import { formatInt, formatMoney } from './format'
import { Panel } from './Panel'

const POLL_INTERVAL_MS = 2500

// A per-cycle rate reads more clearly framed the way the bank quotes it: rate × 50 cycles as a percentage.
function rateNote(ratePerCycle) {
  if (typeof ratePerCycle !== 'number') return '—'
  return `${(ratePerCycle * 100).toFixed(3)}% / cycle · ≈ ${(ratePerCycle * 50 * 100).toFixed(1)}% / 50 cyc`
}

// Roster of lending banks with their per-cycle rate and current outstanding book.
function BanksPage() {
  const [ready, setReady] = useState(false)
  const [loadError, setLoadError] = useState(null)
  const [banks, setBanks] = useState([])

  const loadAll = useCallback(async () => {
    try {
      const result = await api.getBanks()
      setBanks(result ?? [])
      setLoadError(null)
    } catch (error) {
      setLoadError(error.message)
    } finally {
      setReady(true)
    }
  }, [])

  useEffect(() => {
    const initialId = setTimeout(loadAll, 0)
    const intervalId = setInterval(loadAll, POLL_INTERVAL_MS)
    return () => {
      clearTimeout(initialId)
      clearInterval(intervalId)
    }
  }, [loadAll])

  return (
    <main className="main">
      {!ready ? (
        <section className="placeholder" aria-busy="true">
          <span className="spinner" aria-hidden="true" />
          <p>Loading banks…</p>
        </section>
      ) : (
        <>
          {loadError ? (
            <div className="banner" role="alert">
              <strong>Showing last known state.</strong>
              <span>{loadError}</span>
            </div>
          ) : null}

          <Panel title="Banks" count={`${formatInt(banks.length)}`} className="panel-holdings">
            {banks.length === 0 ? (
              <p className="note">No banks yet. One is created the first time a trader borrows.</p>
            ) : (
              <div className="tbl-wrap">
                <table className="tbl">
                  <thead>
                    <tr>
                      <th scope="col">Bank</th>
                      <th scope="col">Interest rate</th>
                      <th scope="col" className="ta-r">
                        Revenue
                      </th>
                      <th scope="col" className="ta-r">
                        Open loans
                      </th>
                      <th scope="col" className="ta-r">
                        Outstanding
                      </th>
                    </tr>
                  </thead>
                  <tbody>
                    {banks.map((bank) => (
                      <tr key={bank.id}>
                        <th scope="row" className="cell-ellipsis">
                          {bank.name}
                        </th>
                        <td className="num">{rateNote(bank.interestRatePerCycle)}</td>
                        <td className="num ta-r">{formatMoney(bank.balance)}</td>
                        <td className="num ta-r">{formatInt(bank.openLoanCount)}</td>
                        <td className="num ta-r">{formatMoney(bank.outstandingPrincipal)}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}
          </Panel>
        </>
      )}
    </main>
  )
}

export default BanksPage
