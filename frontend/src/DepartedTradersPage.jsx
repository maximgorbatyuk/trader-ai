import { useCallback, useEffect, useState } from 'react'
import { Link } from 'react-router-dom'
import './App.css'
import { api } from './api'
import { formatInt, formatMoney, formatSigned, toneOf } from './format'
import { Panel } from './Panel'

const POLL_INTERVAL_MS = 2500
const REASON_LABEL = { FundLoss: 'Fund loss', Starvation: 'Starvation' }

function DepartedTradersPage() {
  const [ready, setReady] = useState(false)
  const [loadError, setLoadError] = useState(null)
  const [exits, setExits] = useState([])

  const loadAll = useCallback(async () => {
    try {
      const data = await api.getMarketExits(50)
      setExits(data ?? [])
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
    <div className="app">
      <header className="topbar">
        <Link className="brand" to="/" aria-label="Back to the Trader AI dashboard">
          <span className="brand-mark" aria-hidden="true">
            TA
          </span>
          <span className="brand-name">Trader&nbsp;AI</span>
          <span className="brand-tag" aria-hidden="true">
            Departed traders
          </span>
        </Link>
        <Link className="btn" to="/">
          ← Dashboard
        </Link>
      </header>

      <main className="main">
        {!ready ? (
          <section className="placeholder" aria-busy="true">
            <span className="spinner" aria-hidden="true" />
            <p>Loading departed traders…</p>
          </section>
        ) : (
          <>
            {loadError ? (
              <div className="banner" role="alert">
                <strong>Showing last known state.</strong>
                <span>{loadError}</span>
              </div>
            ) : null}

            <Panel title="Departed traders" count={`${exits.length}`} className="panel-holdings">
              {exits.length === 0 ? (
                <p className="note">No traders have left the market yet.</p>
              ) : (
                <div className="tbl-scroll">
                  <table className="tbl">
                    <thead>
                      <tr>
                        <th scope="col">Name</th>
                        <th scope="col">Reason</th>
                        <th scope="col" className="ta-r">
                          Joined
                        </th>
                        <th scope="col" className="ta-r">
                          Left
                        </th>
                        <th scope="col" className="ta-r">
                          Orders
                        </th>
                        <th scope="col" className="ta-r">
                          Initial balance
                        </th>
                        <th scope="col" className="ta-r">
                          Max worth
                        </th>
                        <th scope="col" className="ta-r">
                          Quit balance
                        </th>
                        <th scope="col" className="ta-r">
                          Net
                        </th>
                      </tr>
                    </thead>
                    <tbody>
                      {exits.map((exit) => {
                        const net = exit.quitBalance - exit.initialBalance
                        return (
                          <tr key={exit.participantId}>
                            <th scope="row" className="cell-ellipsis">
                              {exit.participantName}
                            </th>
                            <td>
                              <span className="tag">{REASON_LABEL[exit.reason] ?? exit.reason}</span>
                            </td>
                            <td className="num ta-r">cycle {formatInt(exit.joinedInCycleNumber)}</td>
                            <td className="num ta-r">cycle {formatInt(exit.leftInCycleNumber)}</td>
                            <td className="num ta-r">{formatInt(exit.ordersPlaced)}</td>
                            <td className="num ta-r">{formatMoney(exit.initialBalance)}</td>
                            <td className="num ta-r">{formatMoney(exit.maxTotalWorth)}</td>
                            <td className="num ta-r">{formatMoney(exit.quitBalance)}</td>
                            <td className={`num ta-r tone-${toneOf(net)}`}>{formatSigned(net)}</td>
                          </tr>
                        )
                      })}
                    </tbody>
                  </table>
                </div>
              )}
            </Panel>
          </>
        )}
      </main>
    </div>
  )
}

export default DepartedTradersPage
