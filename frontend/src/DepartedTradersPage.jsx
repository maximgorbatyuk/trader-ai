import { useCallback, useEffect, useState } from 'react'
import './App.css'
import { api } from './api'
import { formatInt, formatMoney, formatSigned, toneOf } from './format'
import { Panel } from './Panel'

const POLL_INTERVAL_MS = 2500
const REASON_LABEL = { FundLoss: 'Fund loss', Starvation: 'Starvation' }

// Archived departure view embedded in the Traders roster. The existing endpoint intentionally returns the most
// recent fifty departures, so this view preserves that behavior instead of inventing client-side pagination.
function DepartedTradersView({ statusControl }) {
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
    <>
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
            <div className="roster-toolbar">{statusControl}</div>
            {exits.length === 0 ? (
              <p className="note">No traders have left the market yet.</p>
            ) : (
              <div className="tbl-wrap">
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
    </>
  )
}

export default DepartedTradersView
