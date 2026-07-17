import { useCallback, useRef, useState } from 'react'

const BASE_WIDTH = 720
const BASE_HEIGHT = 220

// Area-and-line sparkline. Passing formatValue turns on a y-axis value scale in a left gutter — used by the
// company capitalisation chart; without it the chart renders bare as before. Passing cycles (one entry per
// value) adds vertical lines and cycle labels, xLabel/yLabel add axis titles, and fill grows the chart to its
// container's height with proportionally more gridlines.
export function LineChart({ values, tone, formatValue, label, cycles, xLabel, yLabel, fill = false }) {
  // Aspect ratio (height / width) of the container, used to stretch the viewBox to the panel height while
  // keeping the width at BASE_WIDTH so text and stroke sizes match the fixed-size layout exactly.
  const [ratio, setRatio] = useState(null)
  const teardownRef = useRef(null)
  const containerRef = useCallback(
    (node) => {
      if (teardownRef.current) {
        teardownRef.current()
        teardownRef.current = null
      }
      if (!node || !fill || typeof ResizeObserver === 'undefined') return

      const measure = () => {
        const { clientWidth, clientHeight } = node
        if (clientWidth > 0 && clientHeight > 0) {
          const next = clientHeight / clientWidth
          setRatio((prev) => (prev === next ? prev : next))
        }
      }
      measure()
      const observer = new ResizeObserver(measure)
      observer.observe(node)
      teardownRef.current = () => observer.disconnect()
    },
    [fill],
  )

  const width = BASE_WIDTH
  const height = fill && ratio ? Math.round(width * ratio) : BASE_HEIGHT
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

  // Space horizontal guides regularly; a taller (fill) plot earns more of them so the extra vertical room
  // reads as scale rather than emptiness. The non-fill case keeps the original three interior lines.
  const gridDivisions = fill ? Math.min(12, Math.max(4, Math.round(plot / 40))) : 4
  const gridFractions = []
  for (let step = 1; step < gridDivisions; step += 1) gridFractions.push(step / gridDivisions)

  // A value label sits on every guide when filling, otherwise the original max / mid / min triple.
  const tickDivisions = formatValue ? (fill ? gridDivisions : 2) : 0
  const ticks = []
  let previousLabel = null
  for (let step = 0; step <= tickDivisions && formatValue; step += 1) {
    const fraction = step / tickDivisions
    const text = formatValue(max - fraction * range)
    // Many guides at a coarse compact format (e.g. billions) can round to the same label; in fill mode drop
    // the repeats so every printed value is distinct while still landing on a guide.
    if (fill && text === previousLabel) continue
    previousLabel = text
    ticks.push({ y: plotTop + fraction * plot, text })
  }

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
    <div className={`chart tone-${tone}${fill ? ' chart-fill' : ''}`} ref={containerRef}>
      <svg viewBox={`0 0 ${width} ${height}`} role="img" aria-label={label ?? 'Line chart'}>
        <defs>
          <linearGradient id="chart-fill" x1="0" x2="0" y1="0" y2="1">
            <stop offset="0%" stopColor="currentColor" stopOpacity="0.16" />
            <stop offset="100%" stopColor="currentColor" stopOpacity="0" />
          </linearGradient>
        </defs>
        <g className="chart-grid" aria-hidden="true">
          {gridFractions.map((fraction) => (
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
            {tick.text}
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
