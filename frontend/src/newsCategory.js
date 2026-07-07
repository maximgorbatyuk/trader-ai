// A structural corporate action gets a coloured newswire card and a short text tag so the flag is never
// colour-only. Ordinary and manual posts (General) get no special treatment.
const NEWS_CATEGORY = {
  CompanyClosed: { className: 'news-closed', label: 'Delisted' },
  StockSplit: { className: 'news-stock', label: 'Stock split' },
  StockMerge: { className: 'news-stock', label: 'Reverse split' },
}

export function newsCategoryStyle(category) {
  return NEWS_CATEGORY[category] ?? null
}
