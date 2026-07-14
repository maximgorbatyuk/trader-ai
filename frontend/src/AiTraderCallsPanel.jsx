import { useCallback, useEffect, useState } from 'react'
import './aiTrader.css'
import { api } from './api'
import { Panel } from './Panel'
import { Pager } from './TableControls'
import { AiTraderCallModal } from './AiTraderCallModal'

const PAGE_SIZE = 20

function formatTimestamp(value) {
  if (!value) return '—'
  const date = new Date(value)
  return Number.isNaN(date.getTime()) ? '—' : date.toLocaleString()
}

// Server-paged AI-call history for a trader, newest first. Shown for current AI agents and for converted-back
// Individuals that still have recorded history; hidden for a trader that was never automated. Full request and
// response JSON are loaded lazily only when one call is opened.
export function AiTraderCallsPanel({ participantId, isAiTrader }) {
  const [page, setPage] = useState(1)
  const [data, setData] = useState({ items: [], total: 0, page: 1, pageSize: PAGE_SIZE })
  const [error, setError] = useState(null)
  const [ready, setReady] = useState(false)
  const [selectedCallId, setSelectedCallId] = useState(null)

  const load = useCallback(async () => {
    try {
      const result = await api.getParticipantAiCalls(participantId, page, PAGE_SIZE)
      setData(result ?? { items: [], total: 0, page, pageSize: PAGE_SIZE })
      setError(null)
    } catch (loadError) {
      setError(loadError.message)
    } finally {
      setReady(true)
    }
  }, [participantId, page])

  // Deferred so the fetch does not update state synchronously inside the effect; the parent remounts this panel
  // per participant (via key), so page state resets on trader switch without a setState-in-effect.
  useEffect(() => {
    const initialId = setTimeout(load, 0)
    return () => clearTimeout(initialId)
  }, [load])

  const pageCount = Math.max(1, Math.ceil(data.total / PAGE_SIZE))

  if (ready && data.total === 0 && !isAiTrader) {
    return null
  }

  return (
    <Panel title="AI calls" count={`${data.total}`} className="panel-ai-calls">
      {error ? (
        <p className="command-error" role="alert">
          {error}
        </p>
      ) : null}
      {data.items.length === 0 ? (
        <p className="note">No AI calls recorded yet.</p>
      ) : (
        <div className="tbl-wrap">
          <table className="tbl">
            <thead>
              <tr>
                <th scope="col">Requested</th>
                <th scope="col" className="ta-r">Cycle</th>
                <th scope="col">Provider / model</th>
                <th scope="col">Status</th>
                <th scope="col" className="ta-r">Duration</th>
                <th scope="col">Summary</th>
                <th scope="col" className="ta-r">Applied / rejected</th>
                <th scope="col" aria-label="Details" />
              </tr>
            </thead>
            <tbody>
              {data.items.map((call) => (
                <tr key={call.id}>
                  <td>{formatTimestamp(call.requestedAt)}</td>
                  <td className="num ta-r">{call.snapshotCycleNumber}</td>
                  <td className="cell-ellipsis">
                    {call.providerLabel} · {call.model}
                  </td>
                  <td>
                    <span className="tag">{call.status}</span>
                  </td>
                  <td className="num ta-r">{call.durationMilliseconds != null ? `${call.durationMilliseconds} ms` : '—'}</td>
                  <td className="cell-ellipsis">{call.summary ?? '—'}</td>
                  <td className="num ta-r">
                    {call.appliedOrders} / {call.rejectedOrders}
                  </td>
                  <td className="ta-r">
                    <button type="button" className="btn" onClick={() => setSelectedCallId(call.id)}>
                      Details
                    </button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
      <Pager page={data.page} pageCount={pageCount} onPage={setPage} />
      {selectedCallId != null ? (
        <AiTraderCallModal
          participantId={participantId}
          callId={selectedCallId}
          onClose={() => setSelectedCallId(null)}
        />
      ) : null}
    </Panel>
  )
}
