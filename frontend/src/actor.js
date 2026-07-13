// Shared helpers for the Player / Player's-fund actor switch used by the dashboard and the Trade market page.

export function holdingCompanyIdSet(holdings) {
  return new Set(holdings.filter((holding) => holding.shares > 0).map((holding) => holding.companyId))
}

// Resolves the participant the order book trades as: the player, or their managed fund when the fund is
// selected. Null when the fund is selected but none exists yet, which leaves the book read-only.
export function resolveActor(player, actorKind) {
  if (!player) return null
  if (actorKind === 'fund') {
    return player.fundParticipantId != null
      ? {
          kind: 'fund',
          id: player.fundParticipantId,
          name: player.fundName,
          availableBalance: player.fundAvailableBalance ?? 0,
          margin: player.fundMargin ?? null,
          buyingPower: player.fundMargin?.buyingPower ?? player.fundAvailableBalance ?? 0,
        }
      : null
  }
  return {
    kind: 'player',
    id: player.id,
    name: player.name,
    availableBalance: player.availableBalance ?? 0,
    margin: player.margin ?? null,
    buyingPower: player.margin?.buyingPower ?? player.availableBalance ?? 0,
  }
}

// The order book's read-only hint when the fund is selected but not yet created.
export function emptyActorHintFor(player, actorKind) {
  return actorKind === 'fund' && player && player.fundParticipantId == null
    ? 'Create a fund to trade as the fund.'
    : undefined
}
