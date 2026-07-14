const DAY_COUNT = 3
const ROW_COUNT = 6
const COLUMNS_PER_DAY = 7
const CELLS_PER_DAY = ROW_COUNT * COLUMNS_PER_DAY
const LEVEL_COUNT = 5

function rangeFor(index, count, cyclesPerDay) {
  return {
    start: Math.floor((index * cyclesPerDay) / count) + 1,
    end: Math.floor(((index + 1) * cyclesPerDay) / count),
  }
}

export function ordersActivityCellLabel(cell) {
  if (cell.dayNumber == null) return 'No earlier trading day in this market'

  const range = `Day ${cell.dayNumber}, cycles ${cell.cycleStart}–${cell.cycleEnd}`
  if (!cell.hasData) return `${range}: not reached`

  const orders = `${cell.ordersPlaced} ${cell.ordersPlaced === 1 ? 'order' : 'orders'}`
  const details = []
  if (cell.isPartial) details.push(`in progress through cycle ${cell.observedCycleEnd}`)
  if (cell.paidDividend) details.push('dividend paid')
  return `${range}: ${orders}${details.length ? `, ${details.join(', ')}` : ''}`
}

export function heatmapPositionAfterKey(row, column, key) {
  switch (key) {
    case 'ArrowLeft':
      return { row, column: Math.max(0, column - 1) }
    case 'ArrowRight':
      return { row, column: Math.min(DAY_COUNT * COLUMNS_PER_DAY - 1, column + 1) }
    case 'ArrowUp':
      return { row: Math.max(0, row - 1), column }
    case 'ArrowDown':
      return { row: Math.min(ROW_COUNT - 1, row + 1), column }
    case 'Home':
      return { row, column: 0 }
    case 'End':
      return { row, column: DAY_COUNT * COLUMNS_PER_DAY - 1 }
    default:
      return null
  }
}

export function buildOrdersActivityHeatmap(activity, cyclesPerDay) {
  const dayLength = Math.max(CELLS_PER_DAY, Math.trunc(cyclesPerDay))
  const availableDays = [...new Set(activity.map((point) => point.tradingDayNumber))]
    .filter((dayNumber) => Number.isInteger(dayNumber) && dayNumber > 0)
    .sort((left, right) => left - right)
    .slice(-DAY_COUNT)
  const days = [...Array(DAY_COUNT - availableDays.length).fill(null), ...availableDays]
  const dayIndexByNumber = new Map(days.map((dayNumber, index) => [dayNumber, index]))
  const rowRanges = Array.from({ length: ROW_COUNT }, (_, row) => rangeFor(row, ROW_COUNT, dayLength))
  const rows = Array.from({ length: ROW_COUNT }, (_, row) =>
    days.flatMap((dayNumber) =>
      Array.from({ length: COLUMNS_PER_DAY }, (_, column) => {
        const cellRange = rangeFor(row * COLUMNS_PER_DAY + column, CELLS_PER_DAY, dayLength)
        return {
          dayNumber,
          cycleStart: cellRange.start,
          cycleEnd: cellRange.end,
          ordersPlaced: 0,
          hasData: false,
          paidDividend: false,
          level: 0,
          observedCycleEnd: null,
          isPartial: false,
        }
      }),
    ),
  )

  for (const point of activity) {
    const dayIndex = dayIndexByNumber.get(point.tradingDayNumber)
    if (dayIndex == null || point.tradingCycleNumber < 1 || point.tradingCycleNumber > dayLength) continue

    const cellIndex = Math.min(
      CELLS_PER_DAY - 1,
      Math.floor(((point.tradingCycleNumber - 1) * CELLS_PER_DAY) / dayLength),
    )
    const row = Math.floor(cellIndex / COLUMNS_PER_DAY)
    const column = dayIndex * COLUMNS_PER_DAY + (cellIndex % COLUMNS_PER_DAY)
    const cell = rows[row][column]
    cell.ordersPlaced += point.ordersPlaced
    cell.hasData = true
    cell.paidDividend ||= point.paidDividend
    cell.observedCycleEnd = Math.max(cell.observedCycleEnd ?? 0, point.tradingCycleNumber)
  }

  const peakOrders = Math.max(0, ...rows.flat().map((cell) => cell.ordersPlaced))
  for (const cell of rows.flat()) {
    cell.isPartial = cell.hasData && cell.observedCycleEnd < cell.cycleEnd
    if (cell.ordersPlaced > 0) {
      cell.level = Math.ceil((cell.ordersPlaced / peakOrders) * LEVEL_COUNT)
    }
  }

  return { days, rowRanges, rows, peakOrders }
}
