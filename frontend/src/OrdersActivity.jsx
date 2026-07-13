import { formatInt } from './format'

const ACTIVITY_WINDOW = 300

// Orders-placed-per-cycle summary and chart. The first cycle can hold a large backlog, so the chart shows a
// recent window to keep the scale readable while the total stays all-time.
export function OrdersActivity({ activity }) {
  const points = activity.slice(-ACTIVITY_WINDOW)
  const total = activity.reduce((sum, point) => sum + point.ordersPlaced, 0)
  const windowCounts = points.map((point) => point.ordersPlaced)
  const peak = windowCounts.length ? Math.max(...windowCounts) : 0
  const hasDividend = points.some((point) => point.paidDividend)

  if (points.length < 2) {
    return <p className="note">Start the loop or step a cycle to see orders placed per loop.</p>
  }

  return (
    <>
      <p className="tabpanel-meta">{formatInt(total)} orders</p>
      <div className="quote">
        <span className="muted-sub">Peak {formatInt(peak)} in last {points.length}</span>
        {hasDividend ? <span className="activity-legend">dividend cycle</span> : null}
      </div>
      <ActivityChart points={points} />
    </>
  )
}

// Line-and-area chart of orders placed per loop, with a labelled count axis (Y) and cycle axis (X).
function ActivityChart({ points }) {
  const width = 720
  const height = 96
  const margin = { top: 10, right: 12, bottom: 24, left: 44 }
  const plotWidth = width - margin.left - margin.right
  const plotHeight = height - margin.top - margin.bottom

  const counts = points.map((point) => point.ordersPlaced)
  const tickCount = 3
  const step = Math.max(1, Math.ceil(Math.max(...counts) / tickCount))
  const yMax = step * tickCount
  const yTicks = Array.from({ length: tickCount + 1 }, (_, index) => index * step)

  const count = points.length
  const x = (index) => margin.left + (count === 1 ? plotWidth / 2 : (index * plotWidth) / (count - 1))
  const y = (value) => margin.top + plotHeight - (value / yMax) * plotHeight
  const baseline = margin.top + plotHeight

  const line = points.map((point, index) => `${x(index)},${y(point.ordersPlaced)}`).join(' ')
  const area = `${x(0)},${baseline} ${line} ${x(count - 1)},${baseline}`
  const last = points.at(-1)

  const indexed = points.map((point, index) => ({ point, index }))
  // Sample labels to a fixed count so a wide window (up to 300 cycles) never overlaps, always keeping the most recent one.
  const labelStep = Math.max(1, Math.ceil(count / 12))
  const xLabels = indexed.filter(({ index }) => index % labelStep === 0 || index === count - 1)
  const dividendLines = indexed.filter(({ point }) => point.paidDividend)

  return (
    <div className="activity-chart">
      <svg
        viewBox={`0 0 ${width} ${height}`}
        role="img"
        aria-label={
          `Orders placed per loop across the last ${count} cycles, peaking at ${Math.max(...counts)}.` +
          (dividendLines.length
            ? ` Dividends were paid in cycle${dividendLines.length > 1 ? 's' : ''} ${dividendLines.map(({ point }) => point.cycleNumber).join(', ')}.`
            : '')
        }
      >
        <defs>
          <linearGradient id="activity-fill" x1="0" x2="0" y1="0" y2="1">
            <stop offset="0%" stopColor="currentColor" stopOpacity="0.18" />
            <stop offset="100%" stopColor="currentColor" stopOpacity="0" />
          </linearGradient>
        </defs>
        <g className="chart-axis" aria-hidden="true">
          {yTicks.map((tick) => (
            <g key={tick}>
              <line className="chart-gridline" x1={margin.left} x2={width - margin.right} y1={y(tick)} y2={y(tick)} />
              <text className="chart-tick chart-tick-y" x={margin.left - 8} y={y(tick)}>
                {tick}
              </text>
            </g>
          ))}
          {xLabels.map(({ index }) => (
            <line
              key={`v-${index}`}
              className="chart-gridline"
              x1={x(index)}
              x2={x(index)}
              y1={margin.top}
              y2={baseline}
            />
          ))}
          {xLabels.map(({ point, index }) => (
            <text key={point.cycleNumber} className="chart-tick chart-tick-x" x={x(index)} y={height - 8}>
              {point.cycleNumber}
            </text>
          ))}
        </g>
        <polygon className="activity-area" points={area} fill="url(#activity-fill)" />
        <polyline className="activity-line" points={line} />
        {/* Dashed so the dividend marker reads without relying on colour alone. */}
        {dividendLines.map(({ index }) => (
          <line
            key={`div-${index}`}
            className="chart-dividend-line"
            x1={x(index)}
            x2={x(index)}
            y1={margin.top}
            y2={baseline}
          />
        ))}
        {last ? <circle className="activity-dot" cx={x(count - 1)} cy={y(last.ordersPlaced)} r="3.5" /> : null}
      </svg>
    </div>
  )
}
