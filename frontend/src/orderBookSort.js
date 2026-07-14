export const ORDER_BOOK_DEFAULT_SORT = {
  Buy: { key: 'gainLoss', direction: 'desc' },
  Sell: { key: 'orderPrice', direction: 'desc' },
}

export function orderBookOwnedShares(actor, holding) {
  if (actor == null) return null
  return holding?.shares ?? 0
}

function isMissing(value) {
  return value == null || (typeof value === 'number' && Number.isNaN(value))
}

function compareValues(left, right) {
  if (typeof left === 'number' && typeof right === 'number') return left - right
  return String(left).localeCompare(String(right))
}

export function sortOrderBookRows(rows, sortKey, direction) {
  return [...rows].sort((left, right) => {
    const leftValue = left.sortValues[sortKey]
    const rightValue = right.sortValues[sortKey]
    const leftMissing = isMissing(leftValue)
    const rightMissing = isMissing(rightValue)
    if (leftMissing !== rightMissing) return leftMissing ? 1 : -1

    const comparison = leftMissing ? 0 : compareValues(leftValue, rightValue)
    if (comparison !== 0) return direction === 'desc' ? -comparison : comparison
    return compareValues(left.order.id, right.order.id)
  })
}
