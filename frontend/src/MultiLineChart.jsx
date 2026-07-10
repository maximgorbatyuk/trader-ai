function pointValue(point) {
  return Number(point?.value ?? point?.sentimentValue)
}

// Blue-to-violet hues avoid the market's green/red semantics. The index also changes the dash pattern after
// the first series, so large rosters retain a stable visual mapping even after hues eventually cycle.
function seriesVisual(index, color) {
  const hue = 205 + ((index * 43) % 115)
  const lightness = 0.43 + (Math.floor(index / 115) % 3) * 0.05
  const chroma = 0.1 + (Math.floor(index / 345) % 2) * 0.025
  const dash = index === 0 ? undefined : `${4 + (index % 7) * 2} ${2 + (Math.floor(index / 7) % 7) * 2}`
  return { color: color ?? `oklch(${lightness.toFixed(2)} ${chroma.toFixed(3)} ${hue})`, dash }
}

function latestValue(series) {
  const point = series.points?.at(-1)
  const value = pointValue(point)
  return Number.isFinite(value) ? value : null
}

// A neutral multi-series chart for comparable chronological values. Colours identify a series, while the
// visible legend names and states every latest value so the chart never depends on colour alone.
export function MultiLineChart({ series, label = 'Multi-series line chart', formatValue = String }) {
  const width = 720
  const height = 250
  const padX = 8
  const padY = 18
  const gutter = 54
  const left = padX + gutter
  const plotWidth = width - left - padX
  const plotHeight = height - padY * 2
  const validSeries = series
    .map((item, index) => ({ item, index }))
    .filter(({ item }) => item.points?.some((point) => Number.isFinite(pointValue(point))))
  const values = validSeries.flatMap(({ item }) => item.points.map(pointValue).filter(Number.isFinite))

  if (validSeries.length === 0 || values.length === 0) {
    return <p className="note">No history recorded yet.</p>
  }

  const min = Math.min(...values)
  const max = Math.max(...values)
  const range = max - min
  const usesCycles = validSeries.every(({ item }) => item.points.every((point) => Number.isFinite(Number(point.cycleNumber))))
  const cycles = usesCycles
    ? [...new Set(validSeries.flatMap(({ item }) => item.points.map((point) => Number(point.cycleNumber))))].sort((a, b) => a - b)
    : []
  const firstCycle = cycles[0]
  const lastCycle = cycles.at(-1)

  function xFor(point, index, pointCount) {
    if (usesCycles && cycles.length > 1) {
      return left + ((Number(point.cycleNumber) - firstCycle) / (lastCycle - firstCycle)) * plotWidth
    }
    if (pointCount <= 1) return left + plotWidth / 2
    return left + (index / (pointCount - 1)) * plotWidth
  }

  function yFor(value) {
    return range === 0 ? padY + plotHeight / 2 : padY + plotHeight - ((value - min) / range) * plotHeight
  }

  const ticks = [max, min + range / 2, min]

  return (
    <div className="multi-chart">
      <svg viewBox={`0 0 ${width} ${height}`} role="img" aria-label={label}>
        <g className="chart-grid" aria-hidden="true">
          {[0, 0.5, 1].map((fraction) => {
            const y = padY + fraction * plotHeight
            return <line key={fraction} x1={left} x2={width - padX} y1={y} y2={y} />
          })}
        </g>
        {ticks.map((value, index) => (
          <text key={index} className="chart-tick chart-tick-y" x={left - 10} y={padY + index * (plotHeight / 2)}>
            {formatValue(value)}
          </text>
        ))}
        {validSeries.map(({ item, index }) => {
          const points = item.points
            .map((point, index) => ({ value: pointValue(point), x: xFor(point, index, item.points.length) }))
            .filter((point) => Number.isFinite(point.value))
            .map((point) => `${point.x},${yFor(point.value)}`)
            .join(' ')
          const visual = seriesVisual(index, item.color)
          return (
            <polyline
              key={item.name}
              className="multi-chart-line"
              points={points}
              style={{ stroke: visual.color, strokeDasharray: visual.dash }}
            />
          )
        })}
      </svg>
      <ul className="multi-chart-legend" aria-label="Series legend">
        {validSeries.map(({ item, index }) => {
          const visual = seriesVisual(index, item.color)
          const value = latestValue(item)
          return (
            <li key={item.name}>
              <svg className="multi-chart-swatch" viewBox="0 0 16 4" aria-hidden="true">
                <line x1="0" x2="16" y1="2" y2="2" style={{ stroke: visual.color, strokeDasharray: visual.dash }} />
              </svg>
              <span>{item.name}</span>
              <span className="num">{value == null ? '—' : formatValue(value)}</span>
            </li>
          )
        })}
      </ul>
    </div>
  )
}
