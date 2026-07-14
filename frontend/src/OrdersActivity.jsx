import { useState } from 'react'
import { formatInt } from './format'
import {
  buildOrdersActivityHeatmap,
  heatmapPositionAfterKey,
  ordersActivityCellLabel,
} from './ordersActivityHeatmap'

export function OrdersActivity({ activity, cyclesPerDay }) {
  const total = activity.reduce((sum, point) => sum + point.ordersPlaced, 0)

  if (activity.length === 0 || !cyclesPerDay) {
    return <p className="note">Start the loop or step a cycle to see orders placed per loop.</p>
  }

  const heatmap = buildOrdersActivityHeatmap(activity, cyclesPerDay)
  const cells = heatmap.rows.flat()
  const hasDividend = cells.some((cell) => cell.paidDividend)
  const hasPartial = cells.some((cell) => cell.isPartial)
  const sampleCell = cells.find((cell) => cell.dayNumber != null)
  const cyclesPerCell = sampleCell ? sampleCell.cycleEnd - sampleCell.cycleStart + 1 : 0

  return (
    <>
      <p className="tabpanel-meta">{formatInt(total)} orders</p>
      <div className="quote">
        <span className="muted-sub">
          Peak {formatInt(heatmap.peakOrders)} per {formatInt(cyclesPerCell)} cycles
        </span>
        {hasDividend ? <span className="activity-legend">dividend cell</span> : null}
        {hasPartial ? <span className="activity-legend activity-legend-partial">partial cell</span> : null}
      </div>
      <ActivityHeatmap heatmap={heatmap} />
    </>
  )
}

function ActivityHeatmap({ heatmap }) {
  const firstAvailableColumn = Math.max(0, heatmap.rows[0].findIndex((cell) => cell.dayNumber != null))
  const [activePosition, setActivePosition] = useState({ row: 0, column: firstAvailableColumn })
  const activeCell = heatmap.rows[activePosition.row]?.[activePosition.column] ?? heatmap.rows[0][0]

  return (
    <figure className="activity-heatmap-figure">
      <div
        className="activity-heatmap-scroll"
        role="region"
        aria-label="Order activity heatmap. Scroll horizontally to view all three trading days."
      >
        <table className="activity-heatmap">
          <caption>Orders placed across the latest three trading days, grouped into intraday cells.</caption>
          <colgroup>
            <col className="activity-heatmap-label-column" />
            <col span="21" />
          </colgroup>
          <thead>
            <tr>
              <th className="activity-heatmap-corner" scope="col">
                Cycles
              </th>
              {heatmap.days.map((dayNumber, index) => (
                <th key={`${dayNumber ?? 'empty'}-${index}`} className="activity-heatmap-day" scope="colgroup" colSpan="7">
                  {dayNumber == null ? 'Earlier day' : `Day ${formatInt(dayNumber)}`}
                </th>
              ))}
            </tr>
          </thead>
          <tbody>
            {heatmap.rows.map((cells, rowIndex) => {
              const rowRange = heatmap.rowRanges[rowIndex]
              return (
                <tr key={rowRange.start}>
                  <th className="activity-heatmap-row" scope="row">
                    {formatInt(rowRange.start)}–{formatInt(rowRange.end)}
                  </th>
                  {cells.map((cell, columnIndex) => {
                    const label = ordersActivityCellLabel(cell)
                    const className = [
                      'activity-heatmap-cell',
                      `level-${cell.level}`,
                      cell.hasData ? '' : 'is-missing',
                      cell.isPartial ? 'is-partial' : '',
                      cell.paidDividend ? 'has-dividend' : '',
                    ]
                      .filter(Boolean)
                      .join(' ')

                    return (
                      <td
                        key={columnIndex}
                        aria-label={label}
                        tabIndex={activePosition.row === rowIndex && activePosition.column === columnIndex ? 0 : -1}
                        data-heatmap-row={rowIndex}
                        data-heatmap-column={columnIndex}
                        onFocus={() => setActivePosition({ row: rowIndex, column: columnIndex })}
                        onMouseEnter={() => setActivePosition({ row: rowIndex, column: columnIndex })}
                        onKeyDown={(event) => {
                          const next = heatmapPositionAfterKey(rowIndex, columnIndex, event.key)
                          if (!next) return

                          event.preventDefault()
                          setActivePosition(next)
                          event.currentTarget
                            .closest('table')
                            ?.querySelector(
                              `[data-heatmap-row="${next.row}"][data-heatmap-column="${next.column}"]`,
                            )
                            ?.focus()
                        }}
                      >
                        <span className={className} title={label} aria-hidden="true" />
                      </td>
                    )
                  })}
                </tr>
              )
            })}
          </tbody>
        </table>
      </div>
      <figcaption className="activity-cell-detail" aria-live="polite">
        {ordersActivityCellLabel(activeCell)}
      </figcaption>
      <div className="activity-scale" aria-label="Heatmap intensity runs from fewer to more orders">
        <span>Less</span>
        {Array.from({ length: 6 }, (_, level) => (
          <span key={level} className={`activity-scale-cell level-${level}`} aria-hidden="true" />
        ))}
        <span>More</span>
      </div>
    </figure>
  )
}
