export function affordability(cash, buyingPower, price) {
  const unitPrice = Number(price)
  if (!(unitPrice > 0)) return { cashShares: 0, marginShares: 0 }
  return {
    cashShares: Math.max(0, Math.floor(Number(cash ?? 0) / unitPrice)),
    marginShares: Math.max(0, Math.floor(Number(buyingPower ?? 0) / unitPrice)),
  }
}

export function maintenanceStanding(value) {
  const standing = Number(value ?? 0)
  return standing >= 0
    ? { excess: standing, deficiency: 0 }
    : { excess: 0, deficiency: Math.abs(standing) }
}
