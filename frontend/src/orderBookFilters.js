export const BUY_FILTER_OPTIONS = [
  { value: 'favorite', label: 'Favorite' },
  { value: 'owned', label: 'Owned' },
  { value: 'all', label: 'All' },
]

export const DEFAULT_BUY_FILTER = 'owned'

export function filterBuyOrders(orders, filter, actorHoldingCompanyIds, companyById) {
  if (filter === 'favorite') {
    return orders.filter((order) => companyById.get(order.companyId)?.isFavorite)
  }
  if (filter === 'owned') {
    return orders.filter((order) => actorHoldingCompanyIds.has(order.companyId))
  }
  return orders
}
