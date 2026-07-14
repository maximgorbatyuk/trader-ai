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
  }

  const peakOrders = Math.max(0, ...rows.flat().map((cell) => cell.ordersPlaced))
  for (const cell of rows.flat()) {
    if (cell.ordersPlaced > 0) {
      cell.level = Math.ceil((cell.ordersPlaced / peakOrders) * LEVEL_COUNT)
    }
  }

  return { days, rowRanges, rows, peakOrders }
}
