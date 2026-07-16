// Area-and-line sparkline. Passing formatValue turns on a y-axis value scale (max / mid / min) in a left
// gutter — used by the company capitalisation chart; without it the chart renders bare as before. Passing
// cycles (one entry per value) adds vertical lines and numeric labels at cycle boundaries, and xLabel/yLabel
// add axis titles.
export function LineChart({ values, tone, formatValue, label, cycles, xLabel, yLabel }) {
  const width = 720
  const height = 220
  const padX = 6
  const padTop = 16
  const hasCycleAxis =
    Array.isArray(cycles) && cycles.length === values.length && values.length > 1 && cycles.every(Number.isFinite)
  const padBottom = 16 + (hasCycleAxis ? 16 : 0) + (xLabel ? 14 : 0)
  const gutter = formatValue ? 72 : 0
  const yTitleRoom = yLabel ? 18 : 0
  const left = padX + gutter + yTitleRoom
  const min = Math.min(...values)
  const max = Math.max(...values)
  const range = max - min
  const plotTop = padTop
  const plotBottom = height - padBottom
  const plot = plotBottom - plotTop
  const plotWidth = width - left - padX

  const points = values.map((value, index) => ({
    x: values.length <= 1 ? left + plotWidth / 2 : left + (index * plotWidth) / (values.length - 1),
    // A flat (zero-range) series centers vertically instead of pinning to the floor.
    y: range === 0 ? height / 2 : plotTop + plot - ((value - min) / range) * plot,
  }))

  const line = points.map((point) => `${point.x},${point.y}`).join(' ')
  const areaBaseline = hasCycleAxis || xLabel ? plotBottom : height
  const area = `${left},${areaBaseline} ${line} ${width - padX},${areaBaseline}`
  const last = points.at(-1)

  const ticks = formatValue
    ? [
        { y: plotTop, value: max },
        { y: plotTop + plot / 2, value: min + range / 2 },
        { y: plotBottom, value: min },
      ]
    : []

  // Vertical guides are spaced evenly across the plot and labelled with the cycle at that point. Snapshots
  // are per-trade, so cycles hold uneven snapshot counts; anchoring the guides to cycle boundaries would
  // bunch them, so we space the guides regularly and let the labels carry the cycle progression instead.
  const cycleMarks = []
  if (hasCycleAxis) {
    const tickCount = Math.min(7, values.length)
    for (let tick = 0; tick < tickCount; tick += 1) {
      const fraction = tickCount === 1 ? 0 : tick / (tickCount - 1)
      cycleMarks.push({
        x: left + fraction * plotWidth,
        cycle: cycles[Math.round(fraction * (values.length - 1))],
        index: tick,
      })
    }
  }

  return (
    <div className={`chart tone-${tone}`}>
      <svg viewBox={`0 0 ${width} ${height}`} role="img" aria-label={label ?? 'Line chart'}>
        <defs>
          <linearGradient id="chart-fill" x1="0" x2="0" y1="0" y2="1">
            <stop offset="0%" stopColor="currentColor" stopOpacity="0.16" />
            <stop offset="100%" stopColor="currentColor" stopOpacity="0" />
          </linearGradient>
        </defs>
        <g className="chart-grid" aria-hidden="true">
          {[0.25, 0.5, 0.75].map((fraction) => (
            <line key={fraction} x1={left} x2={width - padX} y1={plotTop + fraction * plot} y2={plotTop + fraction * plot} />
          ))}
        </g>
        <g aria-hidden="true">
          {cycleMarks.map((mark) => (
            <line key={mark.index} className="chart-gridline" x1={mark.x} x2={mark.x} y1={plotTop} y2={plotBottom} />
          ))}
        </g>
        <polygon points={area} fill="url(#chart-fill)" />
        <polyline className="chart-line" points={line} />
        {last ? <circle className="chart-dot" cx={last.x} cy={last.y} r="3.5" /> : null}
        {ticks.map((tick) => (
          <text key={tick.y} className="chart-tick chart-tick-y" x={left - 10} y={tick.y}>
            {formatValue(tick.value)}
          </text>
        ))}
        {cycleMarks.map((mark) => (
          <text key={`x-${mark.index}`} className="chart-tick chart-tick-x" x={mark.x} y={plotBottom + 12}>
            {mark.cycle}
          </text>
        ))}
        {xLabel ? (
          <text className="chart-tick chart-axis-title" x={left + plotWidth / 2} y={height - 3}>
            {xLabel}
          </text>
        ) : null}
        {yLabel ? (
          <text
            className="chart-tick chart-axis-title"
            transform={`rotate(-90 12 ${plotTop + plot / 2})`}
            x={12}
            y={plotTop + plot / 2}
          >
            {yLabel}
          </text>
        ) : null}
      </svg>
    </div>
  )
}
