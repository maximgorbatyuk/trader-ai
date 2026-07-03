// Area-and-line sparkline. Passing formatValue turns on a y-axis value scale (max / mid / min) in a left
// gutter — used by the company capitalisation chart; without it the chart renders bare as before.
export function LineChart({ values, tone, formatValue, label }) {
  const width = 720
  const height = 220
  const padX = 6
  const padY = 16
  const gutter = formatValue ? 72 : 0
  const left = padX + gutter
  const min = Math.min(...values)
  const max = Math.max(...values)
  const range = max - min
  const plot = height - padY * 2
  const plotWidth = width - left - padX

  const points = values.map((value, index) => ({
    x: left + (index * plotWidth) / (values.length - 1),
    // A flat (zero-range) series centers vertically instead of pinning to the floor.
    y: range === 0 ? height / 2 : padY + plot - ((value - min) / range) * plot,
  }))

  const line = points.map((point) => `${point.x},${point.y}`).join(' ')
  const area = `${left},${height} ${line} ${width - padX},${height}`
  const last = points.at(-1)

  const ticks = formatValue
    ? [
        { y: padY, value: max },
        { y: padY + plot / 2, value: min + range / 2 },
        { y: padY + plot, value: min },
      ]
    : []

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
            <line key={fraction} x1={left} x2={width - padX} y1={padY + fraction * (height - padY * 2)} y2={padY + fraction * (height - padY * 2)} />
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
      </svg>
    </div>
  )
}
