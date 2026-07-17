export function favoriteTraders(participants) {
  return participants.filter((participant) => participant.isFavorite)
}
