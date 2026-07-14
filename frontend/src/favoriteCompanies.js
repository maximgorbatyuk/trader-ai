export function favoriteCompanies(companies) {
  return companies.filter((company) => company.isFavorite)
}

export function matchesFavoriteFilter(company, filter) {
  if (filter === 'favorite') return company.isFavorite
  if (filter === 'not-favorite') return !company.isFavorite
  return true
}
